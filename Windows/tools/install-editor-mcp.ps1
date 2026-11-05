param([string]$ConfigPath = (Join-Path $env:USERPROFILE '.codex/config.toml'))
$ErrorActionPreference = 'Stop'
$taskExecutable = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Compositor.Mcp/bin/Release/net9.0-windows10.0.19041.0/Compositor.Mcp.exe')).Path
$taskConfig = [IO.Path]::GetFullPath($ConfigPath)
$taskOriginal = if (Test-Path -LiteralPath $taskConfig) { [IO.File]::ReadAllText($taskConfig) } else { '' }
if ($taskOriginal -match '(?m)^\s*\[\s*mcp_servers\.compositor\s*\]') {
    if ($taskOriginal.Contains($taskExecutable.Replace('\', '/'))) { Write-Output 'Compositor MCP is already configured.'; exit 0 }
    throw 'An existing compositor MCP entry needs review; it was left unchanged.'
}
$taskParent = [IO.Path]::GetDirectoryName($taskConfig)
[IO.Directory]::CreateDirectory($taskParent) | Out-Null
$taskTable = "`n[mcp_servers.compositor]`ncommand = '" + $taskExecutable.Replace('\', '/') + "'`nstartup_timeout_sec = 15`ntool_timeout_sec = 45`nenabled = true`n"
$taskTemporary = Join-Path $taskParent ('.compositor-mcp-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($taskTemporary, $taskOriginal + $taskTable, [Text.UTF8Encoding]::new($false))
    $taskCurrent = if (Test-Path -LiteralPath $taskConfig) { [IO.File]::ReadAllText($taskConfig) } else { '' }
    if ($taskCurrent -cne $taskOriginal) { throw 'The configuration changed during installation; retry after reviewing it.' }
    [IO.File]::Move($taskTemporary, $taskConfig, $true)
} finally {
    if (Test-Path -LiteralPath $taskTemporary) { Remove-Item -LiteralPath $taskTemporary }
}
Write-Output 'Compositor MCP configured. Restart the MCP connection in Codex to load the tools.'
