param(
    [string]$Executable = "FModel/bin/Debug/net10.0-windows/win-x64/FModel.exe"
)

$resolved = (Resolve-Path $Executable).Path
$process = [Diagnostics.Process]::new()
$process.StartInfo.FileName = $resolved
$process.StartInfo.Arguments = "--mcp"
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.RedirectStandardInput = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$process.StartInfo.CreateNoWindow = $true

try {
    [void]$process.Start()
    $initialize = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"FModel smoke test","version":"1"}}}'
    $process.StandardInput.WriteLine($initialize)
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $process.StandardInput.Flush()

    $first = $process.StandardOutput.ReadLineAsync()
    if (!$first.Wait(10000)) { throw "MCP initialize timed out." }
    $serverName = (($first.Result | ConvertFrom-Json).result.serverInfo.name)
    if ($serverName -notin "FModel", "FModel.Mcp") { throw "Unexpected initialize response." }

    $second = $process.StandardOutput.ReadLineAsync()
    if (!$second.Wait(10000)) { throw "MCP tools/list timed out." }
    $tools = (($second.Result | ConvertFrom-Json).result.tools.name)
    $expected = "fmodel_status", "fmodel_search_assets", "fmodel_list_directory", "fmodel_get_asset_metadata", "fmodel_get_asset_summary", "fmodel_get_asset_dependencies", "fmodel_search_content", "fmodel_find_asset", "fmodel_export_asset", "fmodel_export_batch", "fmodel_render_preview", "fmodel_open_asset", "fmodel_list_game_versions", "fmodel_configure_game", "fmodel_set_aes_keys", "fmodel_set_mappings", "fmodel_get_export_options"
    if ((Compare-Object $expected $tools)) { throw "Registered MCP tools do not match the expected contract." }
    Write-Host "MCP smoke test passed: $($tools -join ', ')"
}
finally {
    if (!$process.HasExited) { $process.Kill($true) }
    $process.Dispose()
}
