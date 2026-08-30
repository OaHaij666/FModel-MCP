# FModel-MCP

> A frozen FModel fork adapted for AI agents through Model Context Protocol (MCP).

This repository is a purpose-built MCP variant of [4sval/FModel](https://github.com/4sval/FModel), pinned to the `aug-2026` upstream release line. It preserves the FModel GUI while exposing the same archive, configuration, asset inspection, preview, and export capabilities to MCP-compatible agents such as Codex.

It is not an official FModel release and intentionally does not follow upstream's rapid update cadence.

## Deliverables

- `FModel.exe` — the normal FModel GUI. It also supports `FModel.exe --mcp` for a stdio MCP host.
- `FModel.Mcp.exe` — dedicated self-contained Windows x64 stdio MCP server for agents.

Both modes reuse FModel's existing game-directory profile, AES keys, mappings, and export defaults. MCP responses never return AES material.

## Connect an agent (Codex)

The agent-side installer is versioned in this repository:

```powershell
.\agents\codex\install-fmodel-mcp.ps1 -Executable "C:\path\to\FModel.Mcp.exe"
```

It registers a global `fmodel` stdio MCP server in Codex. To check the registration:

```powershell
codex mcp list
```

The server must be restarted after using configuration tools that change the active game profile, AES keys, or mappings.

## MCP tools

| Tool | Purpose |
| --- | --- |
| `fmodel_status` | Mounted archives, asset count, mappings and key readiness (no secrets). |
| `fmodel_search_assets` | Text/regex search with AND terms, include/exclude wildcards, extension/type filters, cursor pagination, and optional inline export classes. |
| `fmodel_list_directory` | Navigate a mounted virtual directory and list its direct children. |
| `fmodel_get_asset_metadata`, `fmodel_get_asset_summary` | Full structured metadata or a compact mesh-oriented summary. |
| `fmodel_get_asset_dependencies` | Direct package references plus lightweight reverse path-name candidates. |
| `fmodel_search_content`, `fmodel_find_asset` | Bounded text-content search and an agent-oriented candidate finder. |
| `fmodel_export_asset`, `fmodel_export_batch` | Export raw data, properties, textures, models, worlds, animations, audio, or code. Per-call export options are supported. |
| `fmodel_render_preview`, `fmodel_open_asset` | Texture files or hidden-OpenGL model/world preview plus interchange export. |
| `fmodel_list_game_versions`, `fmodel_configure_game` | Discover and configure game profile settings. |
| `fmodel_set_aes_keys`, `fmodel_set_mappings` | Configure keys and mappings without returning secret values. |
| `fmodel_get_export_options` | Inspect active defaults and allowed export enums, including `Gltf2` / GLB. |

For example, an agent can search `Dalang`, choose the static-mesh `.uasset`, then call `fmodel_export_asset` with `kind: "models"` and `options: { "meshFormat": "Gltf2" }` to produce a `.glb` file.

## Requirements and limitations

- Windows x64 and .NET 10 are required for building. Published self-contained binaries do not need a separate .NET install.
- Users must only configure game files, AES keys, and mappings they are authorized to access.
- Model/world screenshots require a working local OpenGL/GPU driver. Failure to capture a screenshot does not prevent model/world export.
- `FModel.Mcp.exe` serializes operations because the underlying FModel provider is mutable and WPF-bound.

## Upstream FModel

------------------------------------------

[![CI Status](https://img.shields.io/github/actions/workflow/status/4sval/FModel/qa.yml?label=CI)](https://github.com/4sval/FModel/actions)
[![Latest](https://img.shields.io/github/v/release/4sval/FModel?color=yellow)](https://fmodel.app/download)
[![Donate](https://img.shields.io/badge/sponsor-DB61A2?logo=GitHub-Sponsors&logoColor=white)](https://fmodel.app/donate)
[![Discord](https://discord.com/api/guilds/637265123144237061/widget.png?style=shield)](https://fmodel.app/discord)
***

### Description:
FModel is an archive explorer for [Unreal Engine](https://www.unrealengine.com/en-US/) games that uses [CUE4Parse](https://github.com/FabianFG/CUE4Parse) as its core parsing library, providing robust support for the latest UE4 and UE5 archive formats. It aims to deliver a modern and intuitive user interface, powerful features, and a comprehensive set of tools for previewing and converting game packages, empowering YOU to understand games' inner workings with ease.

FModel is actively maintained and developed by a dedicated community of contributors, and welcomes all new contributions and feedback.

### Installation:
For installation, follow the instructions from [here](https://github.com/4sval/FModel/wiki/Installing-FModel)

### Sponsorship:
<p>
  <a href="https://1password.com/">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://cdn.fmodel.app/i/svg/1password-light.svg">
      <source media="(prefers-color-scheme: light)" srcset="https://cdn.fmodel.app/i/svg/1password-dark.svg">
      <img src="https://cdn.fmodel.app/i/svg/1password-light.svg" width="256px">
    </picture>
  </a>
</p>

### License:
FModel is licensed under [GPL-3](https://github.com/4sval/FModel/blob/dev/LICENSE), and licenses of third-party libraries used are listed [here](https://github.com/4sval/FModel/blob/dev/NOTICE).
