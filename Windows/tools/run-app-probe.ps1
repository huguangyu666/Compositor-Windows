param(
    [Parameter(Mandatory)][string[]]$AppPath,
    [Parameter(Mandatory)][string]$LogDirectory,
    [Parameter(Mandatory)][string]$EvidencePrefix,
    [ValidateSet('--logging-probe', '--lifetime-probe', '--watcher-probe', '--mcp-interop-probe', '--recovery-probe', '--transform-probe', '--navigation-probe', '--workspace-probe', '--panel-perf', '--effects-perf', '--stress-matrix', '--retention-probe', '--paint-perf', '--direct-outline-probe', '--project-open-probe', '--project-save-probe', '--draft-exit-probe', '--ai-image-probe')][string]$Argument = '--logging-probe',
    [string]$ReportFile = 'logging-probe.json',
    [int]$ExpectedGroups = 0,
    [int]$TimeoutSeconds = 180,
    [switch]$RequireForeground
)
$ErrorActionPreference = 'Stop'
$portRoot = Split-Path -Parent $PSScriptRoot
$evidenceRoot = Join-Path $portRoot 'artifacts/probe-evidence'
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
# Resolve a relative -LogDirectory against the CALLER's current location, the way the evidence
# prefix is (Join-Path arithmetic over an absolute root): [Path]::GetFullPath alone uses the .NET
# process cwd, which differs from PowerShell's location when the shell was started elsewhere -
# relative logs then landed under artifacts/artifacts while prefix-copied evidence went to the
# intended folder, and the guard passed anyway because both sides still sat under portRoot.
$logRoot = if ([System.IO.Path]::IsPathRooted($LogDirectory)) { [System.IO.Path]::GetFullPath($LogDirectory) }
           else { [System.IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $LogDirectory)) }
if (!$logRoot.StartsWith([System.IO.Path]::GetFullPath($portRoot), [StringComparison]::OrdinalIgnoreCase) -or
    $logRoot -notmatch '[\\/]artifacts[\\/]') {
    throw "The isolated log root must be a scratch directory under $portRoot/artifacts, got $logRoot."
}
$resolvedApps = @($AppPath | ForEach-Object {
    $path = (Resolve-Path -LiteralPath $_).Path
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "App executable is missing: $path" }
    $path
})
foreach ($app in $resolvedApps) {
    $directory = Split-Path -Parent $app
    foreach ($required in @('Compositor.Core.dll', 'Compositor.Renderer.dll', 'Compositor.App.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $directory $required))) { throw "Incomplete app output at $directory (missing $required)." }
    }
    $report = Join-Path $directory $ReportFile
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }
}
if (Test-Path -LiteralPath $logRoot) { Remove-Item -LiteralPath $logRoot -Recurse -Force }
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

# Each instance is identified by its own PID and its own freshly written report, so a leftover
# window or an older JSON cannot be mistaken for this run.
# A build's DLL hashes identify only that build: rebuilding the same commit in another directory
# changes them, because the MVID and the absolute output path are baked into the binaries. The source
# commit is what makes evidence comparable across machines and across the parallel night branches, so
# every entry carries it, plus whether src/tools differ from that commit at run time.
$sourceCommit = ((& git -C $portRoot rev-parse HEAD) | Out-String).Trim()
$productTreeDirty = [bool](((& git -C $portRoot status --porcelain -- src tools) | Out-String).Trim())
$outlineBackend = $(if ($env:COMPOSITOR_DIRECT_OUTLINE -eq '1') { 'direct' } else { 'xaml' })

$hashes = @{}
foreach ($app in $resolvedApps) {
    $directory = Split-Path -Parent $app
    $hashes[$app] = [ordered]@{
        core = (Get-FileHash -LiteralPath (Join-Path $directory 'Compositor.Core.dll') -Algorithm SHA256).Hash
        renderer = (Get-FileHash -LiteralPath (Join-Path $directory 'Compositor.Renderer.dll') -Algorithm SHA256).Hash
        app = (Get-FileHash -LiteralPath (Join-Path $directory 'Compositor.App.dll') -Algorithm SHA256).Hash
    }
}

$startUtc = Get-Date
$previousOverride = $env:COMPOSITOR_LOG_DIRECTORY
$env:COMPOSITOR_LOG_DIRECTORY = $logRoot
$processes = @()
$previousMcpExecutable = $env:COMPOSITOR_MCP_EXECUTABLE
$previousForeground = $env:COMPOSITOR_PROBE_FOREGROUND
$previousPreferences = $env:COMPOSITOR_PREFERENCES_DIRECTORY
if ($Argument -eq '--ai-image-probe') { $env:COMPOSITOR_PREFERENCES_DIRECTORY = Join-Path $logRoot 'preferences' }
if ($RequireForeground) { $env:COMPOSITOR_PROBE_FOREGROUND = '1' }
if ($Argument -in @('--mcp-interop-probe', '--ai-image-probe')) {
    # The editor has to be pointed at a built server rather than guess: ci/ builds MCP Release, while a
    # standalone run usually has a Debug output, and a wrong path here would look like a probe that failed
    # to observe anything.
    $candidate = $env:COMPOSITOR_MCP_EXECUTABLE
    if (-not $candidate) { $candidate = Join-Path $portRoot 'tools/Compositor.Mcp/bin/Release/net9.0-windows10.0.19041.0/Compositor.Mcp.exe' }
    if (!(Test-Path -LiteralPath $candidate)) {
        throw "Compositor.Mcp.exe is missing: $candidate. Build it first (dotnet build tools/Compositor.Mcp -c Release)."
    }
    $env:COMPOSITOR_MCP_EXECUTABLE = (Resolve-Path -LiteralPath $candidate).Path
    Write-Output "MCP server for the interop probe: $($env:COMPOSITOR_MCP_EXECUTABLE)"
}
try {
    foreach ($app in $resolvedApps) {
        $started = & (Join-Path $PSScriptRoot 'start-unpackaged-app.ps1') -FilePath $app -Argument $Argument -Visible:$RequireForeground
        $processes += [ordered]@{ app = $app; process = $started; pid = $started.Id; startedUtc = (Get-Date).ToString('o'); timedOut = $false }
        Write-Output "Started PID=$($started.Id) argument=$Argument logRoot=$logRoot"
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    foreach ($entry in $processes) {
        while (!$entry.process.WaitForExit(1000)) {
            $entry.process.Refresh()
            if ($entry.process.HasExited) { break }
            if ((Get-Date) -gt $deadline) {
                # A driver timeout is not an exit: the same PID is reported and terminated explicitly.
                $entry.timedOut = $true
                $entry.process.Kill()
                $entry.process.WaitForExit(10000) | Out-Null
                break
            }
        }
        $entry.process.WaitForExit()
        $entry.exit = $entry.process.ExitCode
        $entry.elapsedSeconds = ((Get-Date) - (Get-Date $entry.startedUtc)).TotalSeconds
        Write-Output "Exited PID=$($entry.pid) code=$($entry.exit) elapsed=$([math]::Round($entry.elapsedSeconds, 3))s timedOut=$($entry.timedOut)"
    }
} finally {
    if ($null -eq $previousOverride) { Remove-Item Env:COMPOSITOR_LOG_DIRECTORY -ErrorAction SilentlyContinue }
    else { $env:COMPOSITOR_LOG_DIRECTORY = $previousOverride }
    if ($null -eq $previousMcpExecutable) { Remove-Item Env:COMPOSITOR_MCP_EXECUTABLE -ErrorAction SilentlyContinue }
    else { $env:COMPOSITOR_MCP_EXECUTABLE = $previousMcpExecutable }
    if ($null -eq $previousForeground) { Remove-Item Env:COMPOSITOR_PROBE_FOREGROUND -ErrorAction SilentlyContinue }
    else { $env:COMPOSITOR_PROBE_FOREGROUND = $previousForeground }
    if ($null -eq $previousPreferences) { Remove-Item Env:COMPOSITOR_PREFERENCES_DIRECTORY -ErrorAction SilentlyContinue }
    else { $env:COMPOSITOR_PREFERENCES_DIRECTORY = $previousPreferences }
}

$evidence = @()
$logsByProcess = @{}
foreach ($entry in $processes) {
    $reportPath = Join-Path (Split-Path -Parent $entry.app) $ReportFile
    # Two editors launched from one build directory cannot both own that name; the one that lost the
    # race writes <base>-<pid>.json, and that file is the only report belonging to this process.
    $fallbackName = [System.IO.Path]::GetFileNameWithoutExtension($ReportFile) + '-' + $entry.pid + [System.IO.Path]::GetExtension($ReportFile)
    $fallbackPath = Join-Path (Split-Path -Parent $entry.app) $fallbackName
    if (Test-Path -LiteralPath $fallbackPath) { $reportPath = $fallbackPath }
    if (!(Test-Path -LiteralPath $reportPath)) { throw "PID $($entry.pid) left no report: $reportPath" }
    if ((Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $startUtc.ToUniversalTime().AddSeconds(-5)) {
        throw "PID $($entry.pid) reused an old report: $reportPath"
    }
    $raw = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8
    $reportJson = $raw | ConvertFrom-Json
    $logs = @(Get-ChildItem -LiteralPath $logRoot -File -Filter "compositor-app-$($entry.pid).log*" | Sort-Object Name)
    if ($logs.Count -lt 1) { throw "PID $($entry.pid) wrote no log file under $logRoot" }
    $text = ($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    $logsByProcess[$entry.pid] = $text
    # Evidence before the guards, not just before the verdicts: the log checks below throw, and a run that
    # died at one of them left nothing on disk but console scrollback — twice tonight, once for a real
    # flake and once for a machine out of memory. A passing run overwrites both files with the curated copy.
    Set-Content -LiteralPath (Join-Path $evidenceRoot "$EvidencePrefix-$($entry.pid).report.json") -Value $raw -Encoding utf8
    Set-Content -LiteralPath (Join-Path $evidenceRoot "$EvidencePrefix-$($entry.pid).log") -Value $text -Encoding utf8

    # The branch check waits until AFTER the evidence copies, per the comment above them: when this
    # fires, the saved log's missing (or present) line is the proof the throw needs on disk.
    # The label comes from THIS process's environment; the app branch comes from what actually
    # reached the app process. A shell layer that eats the assignment (inline -Command quoting has
    # done exactly that) labels a second xaml walk as the direct one - and a leaked environment
    # variable mislabels the other way. The app prints its truth at startup (MainWindow.xaml.cs:232,
    # only when the flag is live). Both directions must agree. The DEMAND that the line exists
    # applies only to the walk that swaps whole suites (the workspace probe); window-less arguments
    # never construct a MainWindow and would be false alarms. The PROHIBITION - a printed line under
    # a xaml label - is safe everywhere: only a genuinely direct window prints it.
    if ($Argument -eq '--workspace-probe' -and $outlineBackend -eq 'direct' -and -not $text.Contains('backend=direct')) {
        throw "PID $($entry.pid) ran WITHOUT the direct outline branch despite the driver's label: the app log has no 'backend=direct' line."
    }
    if ($outlineBackend -ne 'direct' -and $text.Contains('backend=direct')) {
        throw "PID $($entry.pid) ran the direct outline branch despite the xaml label: the app logged 'backend=direct'. Check the caller's COMPOSITOR_DIRECT_OUTLINE."
    }
    # The gate keeps the audit's "no unexpected exception" check. Expected fault injection and the
    # slow-frame series are counted instead of deleted, and a dropped record fails the run.
    $summary = @($text -split "`r?`n" | Where-Object { $_.StartsWith('logging summary') }) | Select-Object -Last 1
    if ($text -notmatch 'logging summary ') { throw "PID $($entry.pid) never drained: its log has no closing summary." }
    if ($text -match 'Unhandled:|Fatal:|missing resource|probe failed') { throw "PID $($entry.pid) logged an unexpected exception." }
    # A probe run that lost records would make every log-based check below unreliable — except for the one
    # record class that is shed by design: diagnostics over a full bounded backlog. Only the logging probe
    # drives the queue that hard, and only when its own report shows the shed records were counted and no
    # error was lost does a 'dropped N records' notice stay a legal outcome rather than a broken run.
    if ($text -match 'dropped [0-9]+ records') {
        $accountedShed = $Argument -eq '--logging-probe' -and $reportJson.droppedError -eq 0 -and $reportJson.burstAccounted -eq $true
        if (-not $accountedShed) { throw "PID $($entry.pid) dropped log records; the log is not a complete record of this run." }
    }
    if ($summary -match 'droppedError=[1-9]|droppedLate=[1-9]|droppedFault=[1-9]') { throw "PID $($entry.pid) lost records according to its own summary." }
    if ($Argument -eq '--logging-probe') {
        # The probe's own contract, read back from disk after the process left: the error that had to
        # survive the backlog, the record logged after a cancelled close, and the final exit drain.
        foreach ($required in @('kept error', 'logged after a cancelled close', 'last record before close')) {
            if ($text -notmatch "loggingprobe$($entry.pid) $required") { throw "PID $($entry.pid) lost its '$required' record." }
        }
    }
    if ($Argument -eq '--recovery-probe') {
        # The caught close fault only proves anything if it is on disk: the window is gone by the time
        # the script reads this, so the log is the record that the process stayed alive and reported.
        foreach ($required in @('before faulting close request', 'Close confirmation failed', 'before second close request')) {
            if ($text -notmatch [regex]::Escape($required)) { throw "PID $($entry.pid) lost its '$required' record." }
        }
    }
    $faultLines = [ordered]@{
        framePublication = ([regex]::Matches($text, 'Frame publication failed')).Count
        renderWorker = ([regex]::Matches($text, 'worker failed')).Count
        ditherWorker = ([regex]::Matches($text, 'Dither worker failed')).Count
        recoveryCloseFault = ([regex]::Matches($text, 'Close confirmation failed')).Count
        slowFrame = ([regex]::Matches($text, 'slow frame band=')).Count
    }
    # Each probe declares the fault injection it performs, so a count outside that table is either a
    # new failure or an injection guard that stopped being exercised. report.status=passed alone says
    # nothing about either. slowFrame stays a load measurement and is only reported.
    $declaredFaults = @{
        # Three injected dither-worker faults: the two publication-order ones, plus the panel's own
        # "a failed preview says so" claim, which throws where the engine would.
        '--transform-probe'  = @{ framePublication = 3; renderWorker = 3; ditherWorker = 3; recoveryCloseFault = 0 }
        '--recovery-probe'   = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 1 }
        '--project-open-probe' = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--project-save-probe' = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--draft-exit-probe' = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--ai-image-probe'   = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--logging-probe'    = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--lifetime-probe'   = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--watcher-probe'    = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--workspace-probe'  = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--navigation-probe' = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--panel-perf'       = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--effects-perf'     = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--stress-matrix'    = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--retention-probe'  = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--paint-perf'       = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
        '--direct-outline-probe' = @{ framePublication = 0; renderWorker = 0; ditherWorker = 0; recoveryCloseFault = 0 }
    }
    foreach ($family in $declaredFaults[$Argument].Keys) {
        if ($faultLines[$family] -ne $declaredFaults[$Argument][$family]) {
            throw "PID $($entry.pid) logged $($faultLines[$family]) $family fault lines; the $Argument declares $($declaredFaults[$Argument][$family])."
        }
    }
    $groups = @($reportJson.PSObject.Properties | Where-Object { $_.Value -is [string] -and $_.Value -eq 'passed' -and $_.Name -ne 'status' }).Count
    # The logging probe's threefold backlog is a load measurement, not evidence worth committing:
    # only its counted shape is retained next to the records the run actually proves.
    $retained = $text
    $burstLines = 0
    if ($Argument -eq '--logging-probe') {
        $lines = @($text -split "`r?`n")
        $burstLines = @($lines | Where-Object { $_ -match 'loggingprobe\d+ slow frame band=64x64' }).Count
        $retained = (@($lines | Where-Object { $_ -notmatch 'loggingprobe\d+ slow frame band=64x64' }) -join "`n")
    }
    $evidence += [ordered]@{
        pid = $entry.pid
        app = $entry.app
        argument = $Argument
        outlineBackend = $outlineBackend
        startedUtc = $entry.startedUtc
        elapsedSeconds = [math]::Round($entry.elapsedSeconds, 3)
        exitCode = $entry.exit
        timedOut = $entry.timedOut
        reportFile = $ReportFile
        reportStatus = $reportJson.status
        passedGroups = $groups
        logFiles = @($logs | ForEach-Object { [ordered]@{ name = $_.Name; bytes = $_.Length } })
        logBytes = [int](($logs | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum)
        archiveCount = $logs.Count
        burstLinesRetainedAsCount = $burstLines
        summary = $summary
        expectedFaultLines = $faultLines
        sourceCommit = $sourceCommit
        productTreeDirty = $productTreeDirty
        coreSha256 = $hashes[$entry.app].core
        rendererSha256 = $hashes[$entry.app].renderer
        appSha256 = $hashes[$entry.app].app
    }
    # Evidence first, verdict second. A failing run is the one whose raw report and log matter most —
    # throwing before writing left RED rounds and flakes with nothing on disk but console scrollback.
    Set-Content -LiteralPath (Join-Path $evidenceRoot "$EvidencePrefix-$($entry.pid).report.json") -Value $raw -Encoding utf8
    Set-Content -LiteralPath (Join-Path $evidenceRoot "$EvidencePrefix-$($entry.pid).log") -Value $retained -Encoding utf8
    if ($entry.timedOut) { throw "PID $($entry.pid) needed a driver timeout kill." }
    if ($entry.exit -ne 0) { throw "PID $($entry.pid) exited with code $($entry.exit)." }
    if ($reportJson.status -ne 'passed') { throw "PID $($entry.pid) report status=$($reportJson.status): $($reportJson.error)" }
    if ($ExpectedGroups -gt 0 -and $groups -ne $ExpectedGroups) { throw "PID $($entry.pid) passed $groups of $ExpectedGroups groups." }
}

if ($Argument -eq '--logging-probe' -and $processes.Count -gt 1) {
    # Two real editors sharing one log root must not read or overwrite each other's session file.
    foreach ($entry in $evidence) {
        foreach ($other in $evidence) {
            if ($other.pid -eq $entry.pid) { continue }
            if ($logsByProcess[$entry.pid].Contains("loggingprobe$($other.pid) ")) { throw "PID $($entry.pid) log contains PID $($other.pid) records." }
        }
    }
}

$record = [ordered]@{
    probe = $Argument
    logRoot = $logRoot
    startedUtc = $startUtc.ToUniversalTime().ToString('o')
    instances = $processes.Count
    totalLogBytes = [int](($evidence | ForEach-Object { $_.logBytes } | Measure-Object -Sum).Sum)
    processes = $evidence
}
$record | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidenceRoot "$EvidencePrefix-process.json") -Encoding utf8
$record | ConvertTo-Json -Depth 20 -Compress
