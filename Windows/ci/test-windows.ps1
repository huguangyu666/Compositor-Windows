param([string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$portRoot = Split-Path -Parent $PSScriptRoot
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $portRoot 'artifacts/ci-test-results' }
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()
Push-Location $PSScriptRoot
try {
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or !$sdk.StartsWith('9.0.', [StringComparison]::Ordinal)) {
        throw "The headless test entry point requires .NET SDK 9; resolved '$sdk'."
    }
    Write-Output "Windows headless tests: SDK $sdk"
    foreach ($suite in @('App.Infrastructure', 'Core', 'Renderer', 'Segmentation')) {
        $project = Join-Path $portRoot "tests/Compositor.$suite.Tests/Compositor.$suite.Tests.csproj"
        & dotnet test $project --verbosity minimal --results-directory $ResultsDirectory --logger "trx;LogFileName=$suite.trx"
        if ($LASTEXITCODE -ne 0) { $failures.Add($suite) }
    }
    if ($failures.Count -gt 0) { throw "Test suites failed: $($failures -join ', '). See $ResultsDirectory." }
} finally { Pop-Location }
