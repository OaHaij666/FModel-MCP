# FModel MCP host

`FModel.Mcp.exe` is the stdio MCP endpoint for this frozen FModel fork. It reads the
existing `%APPDATA%/FModel/AppSettings.json`; configure a game, AES keys and mappings
in the normal FModel GUI before launching it.

Build the solution with `dotnet build FModel/FModel.slnx -c Release`. Run the host
directly from its build output, or invoke `FModel.exe --mcp`. MCP logs go to stderr
and JSON-RPC stays on stdout.

The server exposes status/search/metadata/export/preview tools, including
`fmodel_open_asset`, plus
`fmodel_list_game_versions`, `fmodel_configure_game`, `fmodel_set_aes_keys`,
`fmodel_set_mappings`, and `fmodel_get_export_options`. AES values and configured game
paths are intentionally never returned by tools. Export calls accept an optional options
object that overrides the saved mesh, texture, material, morph, socket and compression
settings for that request.

Texture preview uses FModel's normal texture exporter. Models and worlds return their
interchange export and attempt a hidden OpenGL render followed by framebuffer capture.
When the GPU/OpenGL context cannot be created, the response keeps the completed export
and explicitly reports that the screenshot is unavailable.
