param([string]$AppPath)
$ErrorActionPreference = 'Stop'
$portRoot = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $portRoot 'artifacts/verify-all-Release-app/Compositor.App.exe' }
$previous = $env:COMPOSITOR_DIRECT_OUTLINE
try {
    # Only this child process receives the experimental renderer setting.
    $env:COMPOSITOR_DIRECT_OUTLINE = '1'
    & (Join-Path $PSScriptRoot 'start-unpackaged-app.ps1') -FilePath $AppPath -Visible
} finally {
    if ($null -eq $previous) { Remove-Item Env:COMPOSITOR_DIRECT_OUTLINE -ErrorAction SilentlyContinue }
    else { $env:COMPOSITOR_DIRECT_OUTLINE = $previous }
}
