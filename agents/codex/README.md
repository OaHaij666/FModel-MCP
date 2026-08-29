# Codex agent integration

`install-fmodel-mcp.ps1` registers FModel as a global stdio MCP server named `fmodel`.

```powershell
.\install-fmodel-mcp.ps1 -Executable "C:\path\to\FModel.Mcp.exe"
```

The script uses `codex mcp add`, so Codex Desktop, Codex CLI, and the Codex IDE extension share the registration. Restart the client or open a new task after installation to refresh its MCP tool list.

The dedicated `FModel.Mcp.exe` is required by the script. The GUI `FModel.exe` also supports an advanced `--mcp` entry point, but it must be registered manually with that argument.
