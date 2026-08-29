# FModel MCP host

`FModel.Mcp.exe` is the stdio MCP endpoint for this frozen FModel fork. It reads the
existing `%APPDATA%/FModel/AppSettings.json`; configure a game, AES keys and mappings
in the normal FModel GUI before launching it.

Build the solution with `dotnet build FModel/FModel.slnx -c Release`. Run the host
directly from its build output, or invoke `FModel.exe --mcp`. MCP logs go to stderr
and JSON-RPC stays on stdout.

The server exposes `fmodel_status`, `fmodel_search_assets`,
`fmodel_get_asset_metadata`, `fmodel_export_asset`, `fmodel_export_batch`, and
`fmodel_render_preview`. AES values and configured game paths are intentionally never
returned by tools.

Texture preview uses FModel's normal texture exporter. Models and worlds return their
interchange export; this first version explicitly reports an unavailable screenshot when
an offscreen OpenGL context cannot be provided.
