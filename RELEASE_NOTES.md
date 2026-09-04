This is the first release built by CI, and the first one that can decode compressed animations.

## Animation support is real now

`CUE4Parse-Natives` is compiled by CMake during the build, and the MSBuild target prints
"CUE4Parse-Natives build failed. Continuing without it." and carries on. Every previously published
binary was therefore built on a machine without a C++ toolchain and shipped with no ACL support, so
`kind: "animations"` could not succeed for any format, with nothing in the build output to indicate
why.

This release builds the native library with `WITH_ACL` enabled and verifies before publishing that
it reached the executables; if it has not, the release fails instead of shipping a crippled binary.

```
Built CUE4Parse-Natives.dll (39.5 KB)
./out/mcp/FModel.Mcp.exe: embedded=True
./out/gui/FModel.exe:     embedded=True
```

Animation export supports `ActorX`, `UEFormat` and `USD`. It does not support `Gltf2`, which is an
exporter limitation rather than a regression.

## Errors now say what happened

The MCP transport collapses thrown tool exceptions into a fixed
`An error occurred invoking 'x'.` notice, so a mistyped path, an unsupported format, and a missing
native dependency were indistinguishable. Inspection and export tools return structured data instead:

```json
{
  "ok": false,
  "error": {
    "code": "name_matches_but_path_differs",
    "message": "'.../S999/Mesh/SK_Weap_M82_206.uasset' is not mounted, but 4 mounted file(s) are named 'SK_Weap_M82_206'.",
    "hint": "The folder is most likely wrong. Retry with one of candidates, or call fmodel_find_asset with the bare asset name.",
    "candidates": [".../S206/Mesh/SK_Weap_M82_206.uasset", "..."]
  }
}
```

- Asset paths resolve leniently. Omitting `.uasset`, or addressing a `.uexp`/`.ubulk` companion,
  finds the real package and reports `resolution: "resolved_with_normalization"`.
- Failures carry the underlying .NET exception and advice matched to its actual cause, so a format
  rejection is never reported as a mappings problem.
- An unreadable settings file was swallowed and then surfaced as a misleading "no game-directory
  configuration" error. The real parse failure is reported now.

## Silent overwrites on export are gone

`kind: "properties"` dumps the raw property tree while a `kind: "models"` export writes a richer
material description - both as `<MaterialName>.json` in the same folder, so the second quietly
replaced the first. Export results now separate `files`, `writtenFiles`, and `overwrittenFiles`.
Overwriting is reported and does not flip `ok`, because it is sometimes intended. Two new parameters
help: `subdirectory` keeps export kinds apart, and `relativePaths` keeps large batches readable.

`ExportResult` already carried `DiskFilePaths`, so the directory scan is now a completeness backstop
rather than the only source of truth, and `summary.unreportedByExporter` names the gap for exporters
that omit paths. Previously a successful export that wrote nothing looked identical to a silent
failure.

## Capability gaps are visible

`fmodel_status` adds `nativesLoaded`, `mappingsLoaded`, and a `limitations` array listing conditions
that degrade output without raising an error. Read it before concluding that a clean export is a
complete one.

`FModel.Mcp.exe` now declares its own version, so `initialize` no longer answers `1.0.0.0` for every
release.

## Also in this build

The discovery tools added after v1.1.0 ship for the first time: `fmodel_find_asset`,
`fmodel_list_directory`, `fmodel_get_asset_summary`, `fmodel_get_asset_dependencies`,
`fmodel_search_content`. `fmodel_search_assets` gained AND terms, include/exclude wildcards,
extension and asset-type filters, and cursor pagination. The release asserts the full 17-tool
contract before publishing.

## Assets

`FModel-MCP-v1.3.0-win-x64.zip` contains `FModel.exe` and `FModel.Mcp.exe`, self-contained win-x64
single files. `SHA256SUMS.txt` lists the checksums of both executables.

```powershell
.\agents\codex\install-fmodel-mcp.ps1 -Executable "C:\path\to\FModel.Mcp.exe"
```
