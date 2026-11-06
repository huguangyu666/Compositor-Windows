param([string]$SdkVersion = '10.0.26100.0')
$ErrorActionPreference = 'Stop'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$fxcPath = Join-Path $sdkRoot "bin/$SdkVersion/x64/fxc.exe"
$shaderRoot = Join-Path $PSScriptRoot '../src/Compositor.App/Shaders'
foreach ($source in Get-ChildItem -LiteralPath $shaderRoot -Filter '*.hlsl') {
    $profile = if ($source.BaseName -eq 'Fullscreen') { 'vs_5_0' } else { 'ps_5_0' }
    $output = Join-Path $shaderRoot ($source.BaseName + '.bin')
    & $fxcPath $source.FullName /nologo /T $profile /E main /Gis /O3 /Fo $output
    if ($LASTEXITCODE -ne 0) { throw "Shader compilation failed: $($source.Name)" }
}
