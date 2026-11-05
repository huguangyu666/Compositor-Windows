param(
    [ValidateSet('headless', 'app', 'stress')][string]$Level = 'headless',
    [string]$AppPath,
    [int]$BuildTimeoutSeconds = 900,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]*$')][string]$EvidencePrefix = 'verify-all',
    [switch]$InteractiveFocusProbes,
    [ValidateSet('xaml', 'direct')][string]$OutlineBackend = 'xaml'
)
$ErrorActionPreference = 'Stop'
# One entry point for the whole local regression, so "I ran the tests" cannot mean three suites while
# the candidate exe and the in-app probes were never touched. Levels are explicit because the app
# level launches real windows: headless is what a fast loop runs, app is what a candidate gets, and
# stress is app plus the two memory probes — see the note above them for why they are a third level
# rather than part of app.
$portRoot = Split-Path -Parent $PSScriptRoot
# Stay inside ci/ so global.json's SDK 9 is the one MSBuild resolves; running the
# builds from Windows/ would let a machine-wide .NET 10 compile WinUI.
Push-Location $PSScriptRoot
$summary = [ordered]@{ level = $Level; startedUtc = (Get-Date).ToUniversalTime().ToString('o'); steps = [System.Collections.Generic.List[object]]::new() }
$summary.evidencePrefix = $EvidencePrefix
$summary.outlineBackend = $OutlineBackend
$previousOutlineBackend = $env:COMPOSITOR_DIRECT_OUTLINE
$env:COMPOSITOR_DIRECT_OUTLINE = $(if ($OutlineBackend -eq 'direct') { '1' } else { '0' })
try {
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or !$sdk.StartsWith('9.0.', [StringComparison]::Ordinal)) {
        throw "verify-all requires .NET SDK 9 from Windows/ci/global.json; resolved '$sdk'."
    }
    $summary.sdk = $sdk

    foreach ($suite in @('App.Infrastructure', 'Core', 'Renderer')) {
        $project = Join-Path $portRoot "tests/Compositor.$suite.Tests/Compositor.$suite.Tests.csproj"
        $trx = Join-Path $portRoot "artifacts/ci-test-results/$suite.trx"
        & dotnet test $project -c Debug --verbosity minimal --results-directory (Join-Path $portRoot 'artifacts/ci-test-results') --logger "trx;LogFileName=$suite.trx"
        if ($LASTEXITCODE -ne 0) { throw "$suite suite failed (exit $LASTEXITCODE)." }
        $text = Get-Content -LiteralPath $trx -Raw
        if ($text -notmatch 'total="(\d+)"') { throw "$suite.trx has no total count." }
        $total = [int]$Matches[1]
        $summary.steps.Add([ordered]@{ step = "headless:$suite"; exit = 0; total = $total; file = $trx })
    }

    $configurations = @('Debug', 'Release')
    foreach ($configuration in $configurations) {
        # Built into the project's own bin/, then staged into the artifacts directory the probes are pointed at.
        # A `-o` directory is not the same thing: the build leaves its two loose XAML binaries (App.xbf,
        # MainWindow.xbf) behind in bin/ and copies only the packed form on, and Microsoft.UI.Xaml.dll then
        # fails the process at init (0xC000027B) before any managed code runs — no log, no report, and the
        # probe driver can only say "left no report". The suite has been passing because its -o directory
        # kept two copies inherited from an older default-path build.
        & dotnet build (Join-Path $portRoot "src/Compositor.App/Compositor.App.csproj") -c $configuration -t:Rebuild -p:Platform=x64 -p:RuntimeIdentifier=win-x64
        if ($LASTEXITCODE -ne 0) { throw "$configuration app build failed (exit $LASTEXITCODE)." }
        # The build leaves two layouts under bin/x64/<Config>/: the bare TFM one and the win-x64 one. Only the
        # RID folder carries the native Win2D and WindowsAppSDK bootstrap DLLs, so the staging source is chosen
        # by what is actually in it, deepest first — picking the first match by name staged an incomplete
        # directory and the probes lost twenty groups over a missing Microsoft.Graphics.Canvas.dll.
        $requiredBinaries = @('Compositor.App.pri', 'App.xbf', 'MainWindow.xbf',
            'Microsoft.Graphics.Canvas.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
        $candidates = @(Get-ChildItem -LiteralPath (Join-Path $portRoot "src/Compositor.App/bin/x64/$configuration") -Recurse -Filter 'Compositor.App.exe' |
            Sort-Object { $_.DirectoryName.Length } -Descending)
        $stagedExe = $candidates | Where-Object {
            $directory = $_.DirectoryName
            -not ($requiredBinaries | Where-Object { -not (Test-Path -LiteralPath (Join-Path $directory $_)) })
        } | Select-Object -First 1
        if ($null -eq $stagedExe) {
            throw "$configuration build staged no complete layout under bin/x64/$configuration " +
                "(looked for: $($requiredBinaries -join ', '); candidates: $($candidates.Count))."
        }
        $staging = $stagedExe.DirectoryName
        $output = Join-Path $portRoot "artifacts/verify-all-$configuration-app/"
        # /MIR, not /E: an earlier round skipped Microsoft.Graphics.Canvas.dll on the first pass and the probe
        # gate only caught it because the required-file check was there. Mirroring means the directory is this
        # build's staging and nothing else — no inherited file can stand in for a missing one, which is what
        # let the old `-o` layout look reproducible for weeks.
        $staged = & robocopy $staging $output /MIR /NFL /NDL /NJH /NJS
        $robocopyExit = $LASTEXITCODE
        if ($robocopyExit -ge 8) {
            throw "robocopy staged $configuration with exit $robocopyExit ($staging -> $output): $($staged | Select-Object -Last 6)"
        }
        foreach ($required in @('Compositor.App.exe', 'Compositor.App.pri', 'App.xbf', 'MainWindow.xbf',
                                'Microsoft.Graphics.Canvas.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path $output $required))) {
                throw "$configuration staging is missing $required in $output."
            }
        }
        $summary.steps.Add([ordered]@{ step = "build:app-$configuration"; exit = 0; stagedFrom = $staging; output = $output })
    }
    & dotnet build (Join-Path $portRoot "tools/Compositor.Mcp/Compositor.Mcp.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "MCP build failed (exit $LASTEXITCODE)." }
    $summary.steps.Add([ordered]@{ step = 'build:mcp-release'; exit = 0 })

    if ($Level -eq 'headless') {
        $summary.verdict = 'passed'
        Write-Output 'verify-all (headless): builds and three suites passed. App probes were NOT run; use -Level app for a candidate.'
        exit 0
    }

    if (!$AppPath) { $AppPath = Join-Path $portRoot 'artifacts/verify-all-Debug-app/Compositor.App.exe' }
    elseif (!$([System.IO.Path]::IsPathRooted($AppPath))) { $AppPath = Join-Path $portRoot $AppPath }
    if (!(Test-Path -LiteralPath $AppPath)) { throw "App executable is missing: $AppPath" }
    # Both stress probes are compiled under #if DEBUG, so a Release exe would answer nothing and the
    # driver would spend its whole timeout finding out.
    if ($Level -eq 'stress' -and $AppPath -match '[\\/][-_]?Release') {
        throw "-Level stress runs the #if DEBUG memory probes; $AppPath is a Release build."
    }

    & (Join-Path $portRoot "tools/verify-port.ps1") -LaunchApp -AppPath $AppPath -McpConfiguration Release
    if ($LASTEXITCODE -ne 0) { throw "verify-port failed (exit $LASTEXITCODE)." }
    $summary.steps.Add([ordered]@{ step = 'verify-port:launch'; exit = 0; app = $AppPath })

    # ExpectedGroups is a gate, not a note: a probe that silently loses a group would otherwise
    # still report status=passed. Counts are the ones the current suite answers with.
    $probes = @(
        @{ argument = '--transform-probe'; report = 'transform-probe.json'; groups = 164; timeout = 400 },
        @{ argument = '--workspace-probe'; report = 'workspace-probe.json'; groups = $(if ($OutlineBackend -eq 'direct') { 206 } else { 207 }); timeout = 300 },
        @{ argument = '--navigation-probe'; report = 'navigation-probe.json'; groups = 20; timeout = 180 },
        @{ argument = '--recovery-probe'; report = 'recovery-probe.json'; groups = 1; timeout = 180 },
        @{ argument = '--project-open-probe'; report = 'project-open-probe.json'; groups = 12; timeout = 90 },
        @{ argument = '--project-save-probe'; report = 'project-save-probe.json'; groups = 11; timeout = 90 },
        @{ argument = '--draft-exit-probe'; report = 'draft-exit-probe.json'; groups = 8; timeout = 90 },
        @{ argument = '--ai-image-probe'; report = 'ai-image-probe.json'; groups = 6; timeout = 90 },
        @{ argument = '--lifetime-probe'; report = 'lifetime-probe.json'; groups = 0; timeout = 180 },
        @{ argument = '--watcher-probe'; report = 'watcher-probe.json'; groups = 0; timeout = 180 },
        @{ argument = '--mcp-interop-probe'; report = 'mcp-interop-probe.json'; groups = 0; timeout = 240 },
        @{ argument = '--logging-probe'; report = 'logging-probe.json'; groups = 0; timeout = 240 },
        @{ argument = '--panel-perf'; report = 'panel-performance.json'; groups = 1; timeout = 240 },
        @{ argument = '--effects-perf'; report = 'effects-performance.json'; groups = 14; timeout = 300 }
    )
    $logs = Join-Path $portRoot "artifacts/$EvidencePrefix-probe-logs"

    # The two memory probes are a third level instead of part of app because of what they cost, not what
    # they claim: each is another real window that peaks around 1.5 GiB on a machine this suite already
    # shares with other agents, and an editor that cannot get memory is a red that says nothing about the
    # candidate. Their gated claims are relative to their own run — the per-size managed-residue plateau,
    # the soak's managed floor, the render target released at the document switch — so they are worth
    # breaking, but they earn their keep for a candidate that touches buffers, caches or disposal, not for
    # one that touches a menu.
    if ($Level -eq 'stress') {
        $probes += @(
            @{ argument = '--stress-matrix'; report = 'stress-matrix.json'; groups = 3; timeout = 300 },
            @{ argument = '--retention-probe'; report = 'retention-probe.json'; groups = 5; timeout = 300 }
        )
    }

    foreach ($probe in $probes) {
        & (Join-Path $portRoot "tools/run-app-probe.ps1") -AppPath $AppPath -LogDirectory $logs -EvidencePrefix ($EvidencePrefix + $probe.argument.Replace('-', '').Replace('probe', '')) `
            -Argument $probe.argument -ReportFile $probe.report -ExpectedGroups $probe.groups -TimeoutSeconds $probe.timeout `
            -RequireForeground:($InteractiveFocusProbes -and $probe.argument -in @('--workspace-probe', '--transform-probe'))
        if ($LASTEXITCODE -ne 0) { throw "$($probe.argument) failed (exit $LASTEXITCODE)." }
        $summary.steps.Add([ordered]@{ step = "probe:$($probe.argument)"; exit = 0; expectedGroups = $probe.groups })
    }

    $summary.verdict = 'passed'
    Write-Output "verify-all ($Level): all probes and the launched smoke passed against $AppPath."
}
finally {
    if ($null -eq $previousOutlineBackend) { Remove-Item Env:COMPOSITOR_DIRECT_OUTLINE -ErrorAction SilentlyContinue }
    else { $env:COMPOSITOR_DIRECT_OUTLINE = $previousOutlineBackend }
    $summary.finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    $path = Join-Path $portRoot 'artifacts/verify-all-summary.json'
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Output "Summary: $path"
    Pop-Location
}
