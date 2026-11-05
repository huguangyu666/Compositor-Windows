param([switch]$LaunchApp, [string]$AppPath, [ValidateSet('Debug', 'Release')][string]$McpConfiguration = 'Debug')
$ErrorActionPreference = 'Stop'
$portRoot = Split-Path -Parent $PSScriptRoot
$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('compositor-port-smoke-' + [Guid]::NewGuid().ToString('N'))
$projectPath = Join-Path $smokeRoot 'smoke.comp'
$imagesPath = Join-Path $projectPath 'images'
$mcpProcess = $null
$appProcess = $null
try {
    New-Item -ItemType Directory -Path $imagesPath -Force | Out-Null
    Add-Type -AssemblyName System.Drawing
    $layerId = [Guid]::NewGuid().ToString().ToUpperInvariant()
    $pngPath = Join-Path $imagesPath ($layerId + '.png')
    # 64x64 noise rather than 4x4 solid: a solid canvas compresses to the same few hundred bytes at every
    # quality, so the export's quality argument could be ignored and nothing downstream would notice.
    $bitmap = [System.Drawing.Bitmap]::new(64, 64)
    try {
        $rng = New-Object System.Random(20261017)
        for ($y = 0; $y -lt 64; $y++) {
            for ($x = 0; $x -lt 64; $x++) {
                $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $rng.Next(0, 256), $rng.Next(0, 256), $rng.Next(0, 256)))
            }
        }
        $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    $manifest = @{
        format = 'com.compositor.project'; version = 11; colorSpace = 'sRGB'
        documentID = [Guid]::NewGuid().ToString().ToUpperInvariant(); width = 64; height = 64; resolution = 300
        activeLayerID = $layerId
        layers = @(@{
            id = $layerId; name = 'Initial text'; isVisible = $true; imageFile = $layerId + '.png'
            transform = @{ origin = @(0, 0); size = @(64, 64); rotation = 0; flipX = $false; flipY = $false; sampling = 'High quality' }
            text = @{ content = 'Text'; fontName = 'Segoe UI'; fontSize = 24; red = 1; green = 0; blue = 0; alignment = 'Left'; tracking = 0; leading = 0 }
        })
    }
    $manifestPath = Join-Path $projectPath 'manifest.json'
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $exportPath = Join-Path $smokeRoot 'export.png'
    $requests = @(
        @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05' } }
        @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
        @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'open_project'; arguments = @{ path = $projectPath } } }
        @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'get_project_state'; arguments = @{} } }
        @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{ name = 'add_layer'; arguments = @{ name = 'Added'; pngPath = $pngPath } } }
        @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'replace_layer_image'; arguments = @{ layerId = $layerId; pngPath = $pngPath } } }
        @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'update_manifest'; arguments = @{ layers = @(@{ id = $layerId; name = 'Updated'; opacity = 0.5 }) } } }
        @{ jsonrpc = '2.0'; id = 8; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $exportPath; format = 'png' } } }
    )
    # Protocol failures the client has to be able to tell apart: unparsable input, an unknown
    # method, an unknown tool, and an export whose folder does not exist (which must not be built).
    $missingExportFolder = Join-Path $smokeRoot 'not-created'
    $badRequests = @(
        @{ jsonrpc = '2.0'; id = 101; method = 'does/not/exist' }
        @{ jsonrpc = '2.0'; id = 102; method = 'tools/call'; params = @{ name = 'no_such_tool'; arguments = @{} } }
        @{ jsonrpc = '2.0'; id = 103; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = (Join-Path $missingExportFolder 'export.png'); format = 'png' } } }
    )
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    # The configuration has to be named, not assumed: the entry point in ci/ builds MCP Release, while
    # a standalone run usually has a Debug output. Guessing here once made the whole app-level suite
    # depend on whatever leftover build happened to sit in the author's own artifacts directory.
    $start.FileName = Join-Path $portRoot "tools/Compositor.Mcp/bin/$McpConfiguration/net9.0-windows10.0.19041.0/Compositor.Mcp.exe"
    if (!(Test-Path -LiteralPath $start.FileName)) {
        throw "MCP $McpConfiguration executable is missing: $($start.FileName). Build it first (dotnet build tools/Compositor.Mcp -c $McpConfiguration)."
    }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    # Where a relative path in a tool argument lands is the process working directory; pinning it to the smoke
    # folder keeps a verification run from writing next to the repository it is checking, and the folder's own
    # guarded cleanup at the end takes the file with it.
    $start.WorkingDirectory = $smokeRoot
    $mcpProcess = [System.Diagnostics.Process]::Start($start)
    $outputTask = $mcpProcess.StandardOutput.ReadToEndAsync()
    $errorTask = $mcpProcess.StandardError.ReadToEndAsync()
    foreach ($request in $requests) { $mcpProcess.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 12 -Compress)) }
    $mcpProcess.StandardInput.WriteLine('this is not a JSON-RPC line {{{')
    foreach ($request in $badRequests) { $mcpProcess.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 12 -Compress)) }
    # D06: the shapes that used to be fatal rather than merely wrong. A batch array, a bare string and a bare
    # number all have no "method" to read, and reading one off a non-object node throws inside System.Text.Json —
    # measured 2026-10-17 as the whole stdio session dying (exit 0xE0434352) with only the first reply sent.
    # A notification is here too, but as the absence it should be: no line answers it.
    $mcpProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $mcpProcess.StandardInput.WriteLine('[{"jsonrpc":"2.0","id":200,"method":"ping"}]')
    $mcpProcess.StandardInput.WriteLine('"just a string"')
    $mcpProcess.StandardInput.WriteLine('42')
    $mcpProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":201,"method":"tools/call"}')
    # The last line is the claim that survives all of the above: a session that answers this one is still a session.
    $mcpProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":202,"method":"ping"}')
    # D06b: the shapes that used to damage or confuse. A .comp is a directory, so an export pointed at the open
    # package's own manifest.json is a write into the project being served; a file WIC cannot decode used to
    # answer with an empty "Internal error"; and a relative export path lands wherever the server's cwd is,
    # so the reply has to name the place it actually wrote.
    $notAnImage = Join-Path $smokeRoot 'not-an-image.txt'
    Set-Content -LiteralPath $notAnImage -Value 'plain text'
    $mcpProcess.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = 301; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $manifestPath; format = 'png' } } } | ConvertTo-Json -Depth 12 -Compress))
    $mcpProcess.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = 302; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $projectPath; format = 'png' } } } | ConvertTo-Json -Depth 12 -Compress))
    $mcpProcess.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = 303; method = 'tools/call'; params = @{ name = 'add_layer'; arguments = @{ name = 'Not a PNG'; pngPath = $notAnImage } } } | ConvertTo-Json -Depth 12 -Compress))
    $mcpProcess.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = 304; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = 'relative-export.png'; format = 'png' } } } | ConvertTo-Json -Depth 12 -Compress))
    $mcpProcess.StandardInput.WriteLine('{"jsonrpc":"2.0","id":305,"method":"ping"}')
    $mcpProcess.StandardInput.Close()
    if (!$mcpProcess.WaitForExit(15000)) { $mcpProcess.Kill(); throw 'MCP smoke timed out.' }
    if ($mcpProcess.ExitCode -ne 0) { throw $errorTask.Result }
    $responses = @($outputTask.Result -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($responses.Count -ne 22) { throw "Expected 22 responses, got $($responses.Count)." }
    for ($i = 0; $i -lt 8; $i++) {
        if ($responses[$i].id -ne ($i + 1) -or $responses[$i].error -or $responses[$i].result.isError) { throw "RPC failed: $($responses[$i] | ConvertTo-Json -Depth 12 -Compress)" }
    }
    $toolNames = @($responses[1].result.tools.name)
    $expectedTools = @('open_project', 'get_project_state', 'add_layer', 'replace_layer_image', 'update_manifest', 'export_image',
        'editor_list', 'editor_state', 'editor_preview', 'editor_select', 'editor_mask_color', 'editor_ai_start',
        'editor_ai_accept', 'editor_ai_cancel', 'editor_history', 'editor_save', 'editor_open', 'editor_export')
    if ($toolNames.Count -ne $expectedTools.Count -or (Compare-Object ($toolNames | Sort-Object) ($expectedTools | Sort-Object))) { throw 'MCP tool catalog changed.' }
    # The four failures: each must answer (a dropped line taught the client nothing), each must use
    # the code that says which side is wrong, and the bad export must not have built a folder.
    if ($null -ne $responses[8].id -or $responses[8].error.code -ne -32700) { throw "Parse error response wrong: $($responses[8] | ConvertTo-Json -Compress)" }
    if ($responses[9].id -ne 101 -or $responses[9].error.code -ne -32601) { throw "Unknown method should be -32601: $($responses[9] | ConvertTo-Json -Compress)" }
    if ($responses[10].id -ne 102 -or $responses[10].error.code -ne -32602) { throw "Unknown tool should be -32602: $($responses[10] | ConvertTo-Json -Compress)" }
    if ($responses[11].id -ne 103 -or $responses[11].error.code -ne -32602) { throw "Missing export folder should be -32602: $($responses[11] | ConvertTo-Json -Compress)" }
    if (Test-Path -LiteralPath $missingExportFolder) { throw 'export_image created a folder the caller never made.' }
    # The six D06 lines after those: the notification took the 13th slot away by not answering, so indices 12..16
    # are the batch array, the bare string, the bare number, the call with no parameters, and the last ping.
    if ($null -ne $responses[12].id -or $responses[12].error.code -ne -32600) { throw "Batch array should be -32600: $($responses[12] | ConvertTo-Json -Compress)" }
    if ($null -ne $responses[13].id -or $responses[13].error.code -ne -32600) { throw "Bare string should be -32600: $($responses[13] | ConvertTo-Json -Compress)" }
    if ($null -ne $responses[14].id -or $responses[14].error.code -ne -32600) { throw "Bare number should be -32600: $($responses[14] | ConvertTo-Json -Compress)" }
    if ($responses[15].id -ne 201 -or $responses[15].error.code -ne -32602) { throw "Call without params should be -32602: $($responses[15] | ConvertTo-Json -Compress)" }
    if ($responses[16].id -ne 202 -or $responses[16].error) { throw "The session died before the last ping: $($responses[16] | ConvertTo-Json -Compress)" }
    # D06b: writing into the served package must be refused and must have changed nothing; an undecodable file
    # must be answered as the caller's bad argument rather than an empty "Internal error"; and a relative export
    # path must come back as the absolute place it actually wrote.
    if ($responses[17].id -ne 301 -or $responses[17].error.code -ne -32602) { throw "Export into the package should be -32602: $($responses[17] | ConvertTo-Json -Compress)" }
    if ($responses[18].id -ne 302 -or $responses[18].error.code -ne -32602) { throw "Export at the package directory should be -32602: $($responses[18] | ConvertTo-Json -Compress)" }
    if ($responses[19].id -ne 303 -or $responses[19].error.code -ne -32602 -or $responses[19].error.message -notmatch 'decode') { throw "An undecodable PNG should be -32602 saying what could not be decoded: $($responses[19] | ConvertTo-Json -Compress)" }
    $relativeReply = if ($responses[20].result) { $responses[20].result.content[0].text } else { '' }
    if ($responses[20].id -ne 304 -or $relativeReply -notmatch '[A-Za-z]:\\') { throw "A relative export path must come back as an absolute one: $($responses[20] | ConvertTo-Json -Compress)" }
    $written = ($relativeReply -replace '^Exported [0-9]+x[0-9]+ to ', '') -replace ' \([0-9]+ bytes\)\.?$', ''
    if (!(Test-Path -LiteralPath $written)) { throw "The export reply names a file that is not there: $written" }
    # The refusal has to have left the served project readable. A hash taken before the session would race the
    # server's own saves (ids 5-7 write the project on purpose), so the claim is about the end state instead:
    # manifest.json is still the JSON the earlier calls wrote, not the PNG an export would have made of it.
    $servedManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($servedManifest.version -ne 11 -or @($servedManifest.layers).Count -lt 2) { throw 'export_image damaged the manifest of the project it was serving.' }
    if ($responses[21].id -ne 305 -or $responses[21].error) { throw "The session died during the D06b cases: $($responses[21] | ConvertTo-Json -Compress)" }
    foreach ($failure in $responses[8..20]) { Write-Output ('MCP failure transcript: ' + ($failure | ConvertTo-Json -Depth 6 -Compress)) }
    if (!(Test-Path -LiteralPath $exportPath) -or (Get-Item -LiteralPath $exportPath).Length -eq 0) { throw 'No exported PNG.' }
    $saved = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (($saved.layers | Where-Object id -eq $layerId).text) { throw 'Replacing text pixels kept stale text metadata.' }
    Write-Output 'MCP: initialize, discovery of 18 tools and all six package operations passed; parse/unknown-method/unknown-tool/missing-export-folder answer with their own codes and no folder is created; text replacement retires stale metadata; a batch array, a bare string and a bare number are refused as -32600, an unparameterised call as -32602, a notification is not answered, an export aimed at the project being served is refused without touching it, an undecodable file is refused by name, and a relative export path answers with the absolute file it wrote.'
    # D06c: a second session, driven one line at a time, because these three properties only exist between calls:
    # a write must not re-encode the assets that call never touched, a package that moved on disk since the client
    # last saw it must be refused with nothing written, and reading again has to be the way out of that refusal.
    $guardStart = [System.Diagnostics.ProcessStartInfo]::new()
    $guardStart.FileName = $start.FileName
    $guardStart.UseShellExecute = $false
    $guardStart.CreateNoWindow = $true
    $guardStart.RedirectStandardInput = $true
    $guardStart.RedirectStandardOutput = $true
    $guardStart.RedirectStandardError = $true
    $guardStart.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    $guardStart.WorkingDirectory = $smokeRoot
    $guard = [System.Diagnostics.Process]::Start($guardStart)
    function Send-Guard([int]$id, $body) {
        $guard.StandardInput.WriteLine(($body | ConvertTo-Json -Depth 12 -Compress))
        $read = $guard.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(15000)) { throw "MCP session 2 did not answer id $id within 15 s." }
        $line = $read.Result
        if (-not $line) { throw "MCP session 2 closed the pipe on id $id." }
        return $line | ConvertFrom-Json
    }
    try {
        $answered = Send-Guard 401 @{ jsonrpc = '2.0'; id = 401; method = 'tools/call'; params = @{ name = 'open_project'; arguments = @{ path = $projectPath } } }
        if ($answered.error) { throw "Session 2 could not open the project: $($answered | ConvertTo-Json -Compress)" }
        $guardManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $untouched = @($guardManifest.layers | Where-Object { $_.imageFile })[0]
        $untouchedFile = Join-Path $imagesPath $untouched.imageFile
        $untouchedHash = (Get-FileHash -LiteralPath $untouchedFile -Algorithm SHA256).Hash
        # A skipped asset is not opened at all, so its write time is the proof. The hash alone cannot say: this
        # asset was last written by the same encoder, so re-encoding the same pixels leaves identical bytes.
        $untouchedWritten = (Get-Item -LiteralPath $untouchedFile).LastWriteTimeUtc
        $answered = Send-Guard 402 @{ jsonrpc = '2.0'; id = 402; method = 'tools/call'; params = @{ name = 'add_layer'; arguments = @{ name = 'Guarded'; pngPath = $pngPath } } }
        if ($answered.error) { throw "Session 2's add_layer failed: $($answered | ConvertTo-Json -Compress)" }
        $untouchedAfter = Get-Item -LiteralPath $untouchedFile
        if ($untouchedAfter.LastWriteTimeUtc -ne $untouchedWritten) {
            throw 'add_layer re-wrote an asset it never touched, so the saved project is no longer the project it read.'
        }
        if ((Get-FileHash -LiteralPath $untouchedFile -Algorithm SHA256).Hash -ne $untouchedHash) {
            throw 'add_layer changed the bytes of an asset it never touched.'
        }
        # An outside program — the editor, on the other side of this same package — renames a layer that this
        # session never touched. The write is a manifest rewrite; nothing here waits on a race.
        $externalDoc = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $externalTarget = @($externalDoc.layers | Where-Object { $_.id -ne $untouched.id })[0]
        $externalTarget.name = 'Written by another program'
        $externalDoc | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        $rename = @{ jsonrpc = '2.0'; id = 403; method = 'tools/call'; params = @{ name = 'update_manifest'; arguments = @{ layers = @(@{ id = $untouched.id; name = 'Overwritten by session 2' }) } } }
        $answered = Send-Guard 403 $rename
        if ($answered.error.code -ne -32000 -or $answered.error.message -notmatch 'get_project_state') {
            throw "Saving over a package that moved must be refused as -32000 pointing at get_project_state: $($answered | ConvertTo-Json -Compress)"
        }
        if ((Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8) -notmatch 'Written by another program') {
            throw 'The refused save still replaced the other program''s manifest.'
        }
        if ((Get-Item -LiteralPath $untouchedFile).LastWriteTimeUtc -ne $untouchedWritten) {
            throw 'The refused save still wrote an asset, even though ProjectStore promised nothing was written.'
        }
        $answered = Send-Guard 404 @{ jsonrpc = '2.0'; id = 404; method = 'tools/call'; params = @{ name = 'get_project_state'; arguments = @{} } }
        if ($answered.error -or $answered.result.content[0].text -notmatch 'Written by another program') {
            throw "get_project_state must show what is really on disk: $($answered | ConvertTo-Json -Compress)"
        }
        $rename.id = 405
        $answered = Send-Guard 405 $rename
        if ($answered.error) { throw "Reading again has to be the way out of the refusal: $($answered | ConvertTo-Json -Compress)" }
        $afterRetry = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (@($afterRetry.layers | Where-Object { $_.name -eq 'Written by another program' }).Count -ne 1) {
            throw 'The retry after get_project_state lost the other program''s layer.'
        }
        if (@($afterRetry.layers | Where-Object { $_.name -eq 'Overwritten by session 2' }).Count -ne 1) {
            throw 'The retry after get_project_state did not land.'
        }
        # Skipping untouched assets must not turn into skipping the asset this call did change. A replaced layer
        # keeps its own file name, so the only proof that the caller's pixels landed is in that file: it has to
        # carry the new image's dimensions, not the ones that were already there.
        $largerPng = Join-Path $smokeRoot 'larger.png'
        $bigBitmap = [System.Drawing.Bitmap]::new(8, 8)
        try {
            for ($y = 0; $y -lt 8; $y++) { for ($x = 0; $x -lt 8; $x++) { $bigBitmap.SetPixel($x, $y, [System.Drawing.Color]::Lime) } }
            $bigBitmap.Save($largerPng, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $bigBitmap.Dispose() }
        $answered = Send-Guard 406 @{ jsonrpc = '2.0'; id = 406; method = 'tools/call'; params = @{ name = 'replace_layer_image'; arguments = @{ layerId = $untouched.id; pngPath = $largerPng } } }
        if ($answered.error) { throw "replace_layer_image failed: $($answered | ConvertTo-Json -Compress)" }
        $assetBytes = [System.IO.File]::ReadAllBytes($untouchedFile)
        $assetWidth = ($assetBytes[16] * 16777216) + ($assetBytes[17] * 65536) + ($assetBytes[18] * 256) + $assetBytes[19]
        $assetHeight = ($assetBytes[20] * 16777216) + ($assetBytes[21] * 65536) + ($assetBytes[22] * 256) + $assetBytes[23]
        if ($assetWidth -ne 8 -or $assetHeight -ne 8) {
            throw "replace_layer_image left a $($assetWidth)x$($assetHeight) asset where the caller handed 8x8 pixels."
        }
        # The last boundary, and the one the digest cannot see: an outside program rewrites an asset with the same
        # number of bytes, so the package digest (manifest + asset names + lengths) does not move and the save goes
        # ahead. What has to be true afterwards is that going ahead did not mean overwriting — the flip below is
        # inside IEND's CRC field, which the decoder tolerates, so the save is expected to land.
        $blindBytes = [System.IO.File]::ReadAllBytes($untouchedFile)
        $blindIndex = $blindBytes.Length - 6
        $blindBytes[$blindIndex] = [byte]($blindBytes[$blindIndex] -bxor 0xFF)
        [System.IO.File]::WriteAllBytes($untouchedFile, $blindBytes)
        $blind = @{ jsonrpc = '2.0'; id = 407; method = 'tools/call'; params = @{ name = 'update_manifest'; arguments = @{ layers = @(@{ id = $externalTarget.id; name = 'Saved over a same-length rewrite' }) } } }
        $answered = Send-Guard 407 $blind
        if ($answered.error) {
            throw "A same-length asset rewrite is invisible to the digest, so this save had to go through: $($answered | ConvertTo-Json -Compress)"
        }
        if ([System.IO.File]::ReadAllBytes($untouchedFile)[$blindIndex] -ne $blindBytes[$blindIndex]) {
            throw 'The save re-encoded an asset it never touched, so the bytes another program wrote are gone — and the digest never warned about it.'
        }
        # D06e: the four remaining shapes, none of which was ever answered as itself before.
        function PngPpm([string]$path) {
            $bytes = [System.IO.File]::ReadAllBytes($path)
            $offset = 8
            while ($offset + 17 -le $bytes.Length) {
                $length = [int]$bytes[$offset] * 16777216 + [int]$bytes[$offset + 1] * 65536 + [int]$bytes[$offset + 2] * 256 + [int]$bytes[$offset + 3]
                if ([System.Text.Encoding]::ASCII.GetString($bytes, $offset + 4, 4) -eq 'pHYs') {
                    if ($bytes[$offset + 16] -ne 1) { return 'unit is not metre' }
                    return [int]$bytes[$offset + 8] * 16777216 + [int]$bytes[$offset + 9] * 65536 + [int]$bytes[$offset + 10] * 256 + [int]$bytes[$offset + 11]
                }
                $offset += 12 + $length
            }
            return '(no pHYs chunk)'
        }
        # The seed manifest declares 300 PPI, so an export that ignores the document resolution comes back at
        # the encoder's own 96 (3779 ppm) — which is what the agent used to get while the user got 11811.
        $resolutionPng = Join-Path $smokeRoot 'resolution.png'
        $answered = Send-Guard 411 @{ jsonrpc = '2.0'; id = 411; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $resolutionPng; format = 'png' } } }
        if ($answered.error) { throw "the resolution export should have worked: $($answered | ConvertTo-Json -Compress)" }
        if ((PngPpm $resolutionPng) -ne 11811) { throw "export_image dropped the document resolution: pHYs says $(PngPpm $resolutionPng), 300 PPI is 11811 ppm" }
        # The quality argument has to reach the encoder. The fixture is noise, so 5 and 98 differ by a lot.
        $lowJpeg = Join-Path $smokeRoot 'low.jpg'
        $highJpeg = Join-Path $smokeRoot 'high.jpg'
        $answered = Send-Guard 412 @{ jsonrpc = '2.0'; id = 412; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $lowJpeg; format = 'jpeg'; quality = 5 } } }
        if ($answered.error) { throw "the low quality export should have worked: $($answered | ConvertTo-Json -Compress)" }
        $answered = Send-Guard 413 @{ jsonrpc = '2.0'; id = 413; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $highJpeg; format = 'jpg'; quality = 98 } } }
        if ($answered.error) { throw "the high quality export should have worked: $($answered | ConvertTo-Json -Compress)" }
        $lowLength = (Get-Item -LiteralPath $lowJpeg).Length
        $highLength = (Get-Item -LiteralPath $highJpeg).Length
        if ($highLength -lt 2 * $lowLength) { throw "quality=5 gave $lowLength bytes and quality=98 gave $highLength, so the quality argument is not reaching the encoder" }
        # A folder where the tool wants a file is the caller's shape error, and a destination that refuses the
        # write is the file's state — both used to arrive as an unforeseen internal error.
        $folderTarget = Join-Path $smokeRoot 'a-folder'
        New-Item -ItemType Directory -Path $folderTarget -Force | Out-Null
        $answered = Send-Guard 414 @{ jsonrpc = '2.0'; id = 414; method = 'tools/call'; params = @{ name = 'add_layer'; arguments = @{ name = 'Folder'; pngPath = $folderTarget } } }
        if ($answered.error.code -ne -32602 -or $answered.error.message -notmatch 'folder') { throw "a folder as pngPath should be -32602 saying it is a folder: $($answered | ConvertTo-Json -Compress)" }
        $answered = Send-Guard 415 @{ jsonrpc = '2.0'; id = 415; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $folderTarget; format = 'png' } } }
        if ($answered.error.code -ne -32602 -or $answered.error.message -notmatch 'folder') { throw "a folder as the export path should be -32602 saying it is a folder: $($answered | ConvertTo-Json -Compress)" }
        $lockedFile = Join-Path $smokeRoot 'locked.png'
        [System.IO.File]::WriteAllBytes($lockedFile, [byte[]](1, 2, 3))
        Set-ItemProperty -LiteralPath $lockedFile -Name IsReadOnly -Value $true
        try {
            $answered = Send-Guard 416 @{ jsonrpc = '2.0'; id = 416; method = 'tools/call'; params = @{ name = 'export_image'; arguments = @{ path = $lockedFile; format = 'png' } } }
            if ($answered.error.code -ne -32000) { throw "a destination that refuses the write is file state and should be -32000: $($answered | ConvertTo-Json -Compress)" }
            if ((Get-Item -LiteralPath $lockedFile).Length -ne 3) { throw 'the refused export still wrote into the read-only file.' }
        } finally {
            Set-ItemProperty -LiteralPath $lockedFile -Name IsReadOnly -Value $false
        }
        Write-Output 'MCP: a session that did not touch an asset does not re-encode it, a package that moved on disk is refused with nothing written, get_project_state is the way back, an outside same-length rewrite survives a save the digest cannot object to, the export carries the document resolution and the quality argument, and a folder or an unwritable destination answers as itself instead of as an internal error.'
    } finally {
        $guard.StandardInput.Close()
        if (-not $guard.WaitForExit(15000)) { $guard.Kill() }
    }
    if ($LaunchApp) {
        if (!$AppPath) { $AppPath = Join-Path $portRoot 'src/Compositor.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Compositor.App.exe' }
        $appLogRoot = Join-Path $smokeRoot 'app-logs'
        New-Item -ItemType Directory -Path $appLogRoot -Force | Out-Null
        $previousLogDirectory = $env:COMPOSITOR_LOG_DIRECTORY
        $env:COMPOSITOR_LOG_DIRECTORY = $appLogRoot
        try {
            $appProcess = & (Join-Path $PSScriptRoot 'start-unpackaged-app.ps1') -FilePath $AppPath
            Start-Sleep -Seconds 9
            $appProcess.Refresh()
            if ($appProcess.HasExited) { throw "App exited early: $($appProcess.ExitCode)." }
            $logFiles = @(Get-ChildItem -LiteralPath $appLogRoot -File -Filter 'compositor-app-*.log')
            if ($logFiles.Count -eq 0) { throw "The editor wrote no log inside the isolated directory $appLogRoot." }
            $startupLine = (Get-Content -LiteralPath $logFiles[0].FullName -TotalCount 1)
            Write-Output 'App: survived the 9-second startup smoke. Pointer workflows still need interactive verification.'
            Write-Output "App log: isolated under $appLogRoot ($($logFiles.Count) file(s), $((Get-Item -LiteralPath $logFiles[0].FullName).Length) bytes), first line: $startupLine"
        } finally {
            if ($null -eq $previousLogDirectory) { Remove-Item Env:COMPOSITOR_LOG_DIRECTORY -ErrorAction SilentlyContinue }
            else { $env:COMPOSITOR_LOG_DIRECTORY = $previousLogDirectory }
        }
    }
} finally {
    if ($mcpProcess) { if (!$mcpProcess.HasExited) { $mcpProcess.Kill() }; $mcpProcess.Dispose() }
    if ($appProcess) { if (!$appProcess.HasExited) { $null = $appProcess.CloseMainWindow(); if (!$appProcess.WaitForExit(2000)) { $appProcess.Kill() } }; $appProcess.Dispose() }
    $resolvedSmoke = [System.IO.Path]::GetFullPath($smokeRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (!$resolvedSmoke.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path -Leaf $resolvedSmoke).StartsWith('compositor-port-smoke-')) { throw 'Unexpected smoke cleanup path.' }
    if (Test-Path -LiteralPath $resolvedSmoke) {
        # The editor drains its log queue as it closes, so its file can still be held for a moment after the
        # process is gone. Retrying the delete keeps a passed smoke from turning into a failed verification;
        # the directory is under %TEMP% with a run-unique name, so leaving it behind is a leak, not a lie.
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try { Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force -ErrorAction Stop; break }
            catch { if ($attempt -eq 9) { Write-Warning "smoke directory still held, left behind: $resolvedSmoke" } else { Start-Sleep -Milliseconds 200 } }
        }
    }
}
