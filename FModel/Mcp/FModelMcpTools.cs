using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;
using FModel.Extensions;
using FModel.Settings;
using FModel.ViewModels;
using FModel.ViewModels.ApiEndpoints.Models;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Mcp;

[McpServerToolType]
public sealed class FModelMcpTools(FModelMcpRuntime runtime)
{
    private const int DefaultSearchLimit = 100;
    private const int MaximumSearchLimit = 1_000;
    private const int MaximumBatchSize = 25;
    private const int MaximumContentCandidates = 2_000;

    [McpServerTool(Name = "fmodel_list_game_versions"), Description("List supported CUE4Parse/FModel game and Unreal version identifiers for fmodel_configure_game.")]
    public Task<string> ListGameVersions(string filter = null, int limit = 200, CancellationToken cancellationToken = default)
        => runtime.RunSettingsExclusiveAsync(_ =>
        {
            limit = Math.Clamp(limit, 1, 1000);
            var values = Enum.GetNames<EGame>()
                .Where(x => string.IsNullOrWhiteSpace(filter) || x.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(limit).ToArray();
            return Task.FromResult(JsonConvert.SerializeObject(new { count = values.Length, values }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_configure_game"), Description("Create or replace the active FModel game profile. AES values are accepted but never returned. Restart the MCP process after changing an active profile.")]
    public Task<string> ConfigureGame(string gameDirectory, string gameName, string gameVersion, string texturePlatform = null,
        string mainAesKey = null, McpDynamicAesKey[] dynamicAesKeys = null,
        string mappingsFile = null, string mappingsUrl = null, string mappingsJsonPath = null,
        CancellationToken cancellationToken = default)
        => runtime.RunSettingsExclusiveAsync(_ =>
        {
            if (string.IsNullOrWhiteSpace(gameDirectory) || !Path.IsPathFullyQualified(gameDirectory))
                throw new ArgumentException("gameDirectory must be an absolute path.");
            var directory = Path.GetFullPath(gameDirectory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The configured game directory does not exist.");
            if (string.IsNullOrWhiteSpace(gameName)) throw new ArgumentException("gameName is required.");
            if (!Enum.TryParse<EGame>(gameVersion, true, out var version))
                throw new ArgumentException("gameVersion is invalid. Call fmodel_list_game_versions to discover supported values.");

            var profile = DirectorySettings.Default(gameName.Trim(), directory, true, version);
            profile.UeVersion = version;
            if (!string.IsNullOrWhiteSpace(texturePlatform))
            {
                if (!Enum.TryParse<CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform>(texturePlatform, true, out var platform))
                    throw new ArgumentException($"texturePlatform is invalid. Allowed values: {string.Join(", ", Enum.GetNames<CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform>())}.");
                profile.TexturePlatform = platform;
            }
            if (!string.IsNullOrWhiteSpace(mainAesKey) || dynamicAesKeys is { Length: > 0 })
                profile.AesKeys = BuildAesResponse(mainAesKey, dynamicAesKeys);
            if (!string.IsNullOrWhiteSpace(mappingsFile) || !string.IsNullOrWhiteSpace(mappingsUrl) || !string.IsNullOrWhiteSpace(mappingsJsonPath))
                ApplyMappings(profile, mappingsFile, mappingsUrl, mappingsJsonPath);
            UserSettings.Default.GameDirectory = directory;
            UserSettings.Default.CurrentDir = profile;
            UserSettings.Default.PerDirectory[directory] = profile;
            UserSettings.Save();
            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                configured = true,
                gameName = profile.GameName,
                gameVersion = profile.UeVersion.ToString(),
                aesConfigured = profile.AesKeys.IsValid,
                dynamicAesKeyCount = profile.AesKeys.DynamicKeys?.Count ?? 0,
                mappingsConfigured = profile.Endpoints[(int) EEndpointType.Mapping].Overwrite || profile.Endpoints[(int) EEndpointType.Mapping].IsValid,
                restartRequired = runtime.IsInitialized
            }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_set_aes_keys"), Description("Replace AES keys for the active game profile without returning key material. Restart the MCP process after changing keys.")]
    public Task<string> SetAesKeys(string mainAesKey = null, McpDynamicAesKey[] dynamicAesKeys = null, CancellationToken cancellationToken = default)
        => runtime.RunSettingsExclusiveAsync(_ =>
        {
            var profile = RequireCurrentProfile();
            profile.AesKeys = BuildAesResponse(mainAesKey, dynamicAesKeys);
            UserSettings.Save();
            return Task.FromResult(JsonConvert.SerializeObject(new { configured = profile.AesKeys.IsValid, dynamicAesKeyCount = profile.AesKeys.DynamicKeys.Count, restartRequired = runtime.IsInitialized }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_set_mappings"), Description("Configure a local .usmap/.jmap file or mappings endpoint for the active game profile. Restart the MCP process after changing it.")]
    public Task<string> SetMappings(string mappingsFile = null, string mappingsUrl = null, string mappingsJsonPath = null, CancellationToken cancellationToken = default)
        => runtime.RunSettingsExclusiveAsync(_ =>
        {
            var profile = RequireCurrentProfile();
            ApplyMappings(profile, mappingsFile, mappingsUrl, mappingsJsonPath);
            UserSettings.Save();
            var endpoint = profile.Endpoints[(int) EEndpointType.Mapping];
            return Task.FromResult(JsonConvert.SerializeObject(new { configured = endpoint.Overwrite || endpoint.IsValid, source = endpoint.Overwrite ? "file" : "endpoint", restartRequired = runtime.IsInitialized }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_get_export_options"), Description("Return active export defaults and allowed enum values. No secret configuration is returned.")]
    public Task<string> GetExportOptions(CancellationToken cancellationToken = default)
        => runtime.RunSettingsExclusiveAsync(_ =>
        {
            RequireCurrentProfile();
            var options = UserSettings.GetExportOptions();
            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                current = new { options.MeshFormat, options.NaniteMeshFormat, options.MeshQuality, options.TexturePlatform, options.TextureFormat, options.TextureQuality, options.ExportHdrTexturesAsHdr, options.MaterialDepth, options.ExportMaterials, options.ExportMorphTargets, options.SocketFormat, options.CompressionFormat, options.ExportAllTextureMips },
                allowed = new { meshFormat = Enum.GetNames<CUE4Parse_Conversion.Options.EMeshFormat>(), naniteMeshFormat = Enum.GetNames<CUE4Parse_Conversion.Options.ENaniteMeshFormat>(), meshQuality = Enum.GetNames<CUE4Parse_Conversion.Options.EMeshQuality>(), texturePlatform = Enum.GetNames<CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform>(), textureFormat = Enum.GetNames<CUE4Parse_Conversion.Options.ETextureFormat>(), materialDepth = Enum.GetNames<CUE4Parse.UE4.Assets.Exports.Material.EMaterialDepth>(), socketFormat = Enum.GetNames<CUE4Parse_Conversion.Options.ESocketFormat>(), compressionFormat = Enum.GetNames<CUE4Parse_Conversion.Writers.UEFormat.Enums.EFileCompressionFormat>() }
            }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_status"), Description("Report FModel archive mounting, readiness, and optional native capabilities without exposing game paths or keys. Read nativesLoaded and mappingsLoaded before attempting animation or property-heavy exports.")]
    public Task<string> Status(CancellationToken cancellationToken)
        => runtime.RunExclusiveAsync((cue, _) => Task.FromResult(JsonConvert.SerializeObject(new
        {
            ready = cue.Provider.Files.Count > 0,
            projectName = cue.Provider.ProjectName,
            archivesMounted = cue.Provider.MountedVfs.Count,
            archivesPending = cue.Provider.UnloadedVfs.Count,
            assetCount = cue.Provider.Files.Count,
            requiredKeyCount = cue.Provider.RequiredKeys.Count,
            configuredKeyCount = cue.Provider.Keys.Count,
            mappingsLoaded = cue.Provider.MappingsContainer != null,
            nativesLoaded = CUE4Parse.Utils.CUE4ParseNatives.IsInitialized,
            limitations = FModelMcpDiagnostics.ActiveLimitations(cue)
        }, Formatting.Indented)), cancellationToken);

    [McpServerTool(Name = "fmodel_search_assets"), Description("Search mounted FModel asset paths. Results are capped at 1000 entries.")]
    public Task<string> SearchAssets(
        [Description("Text or regular expression to match against asset paths.")] string query,
        [Description("Treat query as a .NET regular expression.")] bool regex = false,
        [Description("Use case-sensitive matching.")] bool caseSensitive = false,
        [Description("Maximum result count, from 1 through 1000.")] int limit = DefaultSearchLimit,
        [Description("Additional text fragments that must all occur in the path (AND semantics).")]
        string[] requiredAll = null,
        [Description("Wildcard path patterns to include. All supplied patterns are ORed.")]
        string[] includePatterns = null,
        [Description("Wildcard path patterns to exclude.")]
        string[] excludePatterns = null,
        [Description("Extensions to return, for example uasset or umap.")]
        string[] extensions = null,
        [Description("Export class names to return, for example UStaticMesh or USkeletalMesh. Type inspection is capped.")]
        string[] assetTypes = null,
        [Description("Zero-based result offset. Prefer nextCursor from the previous response.")]
        int offset = 0,
        [Description("Opaque numeric cursor returned by the previous response.")]
        string cursor = null,
        [Description("Attach export class names to returned uasset/umap entries.")]
        bool includeAssetTypes = false,
        CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.", nameof(query));
            limit = Math.Clamp(limit, 1, MaximumSearchLimit);
            offset = ParseCursor(cursor, offset);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Regex? matcher = regex ? new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)) : null;
            var candidates = cue.Provider.Files.Values
                .Where(file => matcher?.IsMatch(file.Path) ?? file.Path.Contains(query, comparison))
                .Where(file => (requiredAll ?? []).All(term => !string.IsNullOrWhiteSpace(term) && file.Path.Contains(term, comparison)))
                .Where(file => MatchesAnyPattern(file.Path, includePatterns, true))
                .Where(file => !MatchesAnyPattern(file.Path, excludePatterns, false))
                .Where(file => MatchesExtension(file, extensions))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var inspected = 0;
            var typeFilter = assetTypes?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [];
            var filtered = candidates.Where(file =>
            {
                if (typeFilter.Length == 0) return true;
                if (inspected++ >= MaximumContentCandidates) return false;
                try { return runtime.GetAssetTypes(cue, file).Any(type => typeFilter.Contains(type, StringComparer.OrdinalIgnoreCase)); }
                catch { return false; }
            });
            var page = filtered.Skip(offset).Take(limit + 1).ToArray();
            var hasMore = page.Length > limit;
            var results = page.Take(limit)
                .Select(file => BuildSearchResult(cue, file, includeAssetTypes))
                .ToArray();
            return Task.FromResult(JsonConvert.SerializeObject(new { query, regex, count = results.Length, offset, nextCursor = hasMore ? (offset + results.Length).ToString() : null, typeInspectionLimited = typeFilter.Length > 0 && inspected >= MaximumContentCandidates, results }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_list_directory"), Description("List direct child files and folders below a mounted virtual directory. Use this after a path search to navigate precisely.")]
    public Task<string> ListDirectory(string directory, bool recursive = false, string[] extensions = null, int limit = DefaultSearchLimit, int offset = 0, string cursor = null, bool includeAssetTypes = false, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory is required.");
            limit = Math.Clamp(limit, 1, MaximumSearchLimit);
            offset = ParseCursor(cursor, offset);
            var normalized = directory.Replace('\\', '/').Trim('/');
            var prefix = normalized + "/";
            var entries = cue.Provider.Files.Values.Where(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && MatchesExtension(file, extensions))
                .Where(file => recursive || !file.Path[prefix.Length..].Contains('/'))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var folders = cue.Provider.Files.Values.Where(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Path[prefix.Length..]).Where(relative => relative.Contains('/'))
                .Select(relative => relative.Split('/')[0]).Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            var page = entries.Skip(offset).Take(limit + 1).ToArray();
            var hasMore = page.Length > limit;
            return Task.FromResult(JsonConvert.SerializeObject(new { directory = normalized, recursive, folders, count = Math.Min(limit, page.Length), offset, nextCursor = hasMore ? (offset + limit).ToString() : null, files = page.Take(limit).Select(file => BuildSearchResult(cue, file, includeAssetTypes)) }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_get_asset_metadata"), Description("Return FModel's structured metadata for a mounted asset path.")]
    public Task<string> GetAssetMetadata([Description("Exact mounted asset path, or the same path without its .uasset extension.")] string path, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            var lookup = FModelMcpDiagnostics.ResolveFile(cue, path);
            if (!lookup.Found) return Task.FromResult(FModelMcpDiagnostics.FromLookup(lookup, "fmodel_get_asset_metadata"));
            var entry = lookup.Entry;
            if (entry.Extension is not ("uasset" or "umap"))
                return Task.FromResult(JsonConvert.SerializeObject(new { path = entry.Path, extension = entry.Extension, size = entry.Size }, Formatting.Indented));
            try
            {
                var result = cue.Provider.GetLoadPackageResult(entry);
                return Task.FromResult(JsonConvert.SerializeObject(result.GetDisplayData(false), Formatting.Indented));
            }
            catch (Exception exception)
            {
                var mappings = cue.Provider.MappingsContainer != null;
                return Task.FromResult(FModelMcpDiagnostics.Error(
                    FModelMcpDiagnostics.Classify(exception, mappings),
                    FModelMcpDiagnostics.Describe(exception, $"Metadata could not be read for '{entry.Path}'"),
                    FModelMcpDiagnostics.MappingsHint("metadata", mappings),
                    [entry.Path]));
            }
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_get_asset_summary"), Description("Return a compact asset summary: type, object names, skeleton, materials, LOD/geometry counts where available, and direct path-like references.")]
    public Task<string> GetAssetSummary(string path, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            var lookup = FModelMcpDiagnostics.ResolveFile(cue, path);
            if (!lookup.Found) return Task.FromResult(FModelMcpDiagnostics.FromLookup(lookup, "fmodel_get_asset_summary"));
            try
            {
                return Task.FromResult(JsonConvert.SerializeObject(BuildAssetSummary(cue, lookup.Entry), Formatting.Indented));
            }
            catch (Exception exception)
            {
                var mappings = cue.Provider.MappingsContainer != null;
                return Task.FromResult(FModelMcpDiagnostics.Error(
                    FModelMcpDiagnostics.Classify(exception, mappings),
                    FModelMcpDiagnostics.Describe(exception, $"Summary could not be built for '{lookup.Entry.Path}'"),
                    FModelMcpDiagnostics.MappingsHint("summary", mappings),
                    [lookup.Entry.Path]));
            }
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_get_asset_dependencies"), Description("Return direct path references discovered in an asset package and lightweight reverse path-name matches. This is package-level, not a full AssetRegistry graph.")]
    public Task<string> GetAssetDependencies(string path, int limit = 100, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            limit = Math.Clamp(limit, 1, MaximumSearchLimit);
            var lookup = FModelMcpDiagnostics.ResolveFile(cue, path);
            if (!lookup.Found) return Task.FromResult(FModelMcpDiagnostics.FromLookup(lookup, "fmodel_get_asset_dependencies"));
            var entry = lookup.Entry;
            JObject source;
            try { source = JObject.FromObject(BuildAssetSummary(cue, entry)); }
            catch (Exception exception)
            {
                var mappings = cue.Provider.MappingsContainer != null;
                return Task.FromResult(FModelMcpDiagnostics.Error(
                    FModelMcpDiagnostics.Classify(exception, mappings),
                    FModelMcpDiagnostics.Describe(exception, $"Dependencies could not be read for '{entry.Path}'"),
                    FModelMcpDiagnostics.MappingsHint("dependencies", mappings),
                    [entry.Path]));
            }
            var name = Path.GetFileNameWithoutExtension(entry.Name);
            var reverse = cue.Provider.Files.Values.Where(file => !file.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) && file.Path.Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Take(limit).Select(file => new { path = file.Path, extension = file.Extension }).ToArray();
            return Task.FromResult(JsonConvert.SerializeObject(new { path = entry.Path, direct = source["references"], reversePathNameCandidates = reverse, limitation = "Reverse candidates use mounted path names. Content-level reverse references require game-specific registry or content indexing." }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_search_content"), Description("Search decoded text in a bounded set of mounted text-like files (Lua, INI, JSON, CSV, TXT, string/localization assets). Use extension/include filters to keep it precise.")]
    public Task<string> SearchContent(string query, string[] extensions = null, string[] includePatterns = null, int maxCandidates = 500, int limit = 100, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");
            maxCandidates = Math.Clamp(maxCandidates, 1, MaximumContentCandidates);
            limit = Math.Clamp(limit, 1, MaximumSearchLimit);
            var allowed = extensions is { Length: > 0 } ? extensions : ["lua", "luac", "ini", "json", "csv", "txt", "locres", "uasset"];
            var matches = new List<object>();
            var scanned = 0;
            foreach (var file in cue.Provider.Files.Values.Where(file => MatchesExtension(file, allowed) && MatchesAnyPattern(file.Path, includePatterns, true)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Take(maxCandidates))
            {
                scanned++;
                try
                {
                    using var reader = file.CreateReader();
                    var bytes = reader.ReadBytes((int)Math.Min(reader.Length, 2_000_000));
                    var text = Encoding.UTF8.GetString(bytes);
                    if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) text = Encoding.Unicode.GetString(bytes);
                    if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    matches.Add(new { path = file.Path, extension = file.Extension, size = file.Size });
                    if (matches.Count >= limit) break;
                }
                catch { /* An unreadable/encrypted candidate is simply not content-searchable. */ }
            }
            return Task.FromResult(JsonConvert.SerializeObject(new { query, scannedCandidates = scanned, maxCandidates, count = matches.Count, results = matches, limitation = "Binary localization and proprietary table formats may require game-specific decoding; this tool only searches decodable text." }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_find_asset"), Description("Rank candidate assets from one or more names using AND path matching, optional asset type filtering, and concise type data. Use this as the agent-friendly starting point.")]
    public Task<string> FindAsset(string[] terms, string[] assetTypes = null, int limit = 50, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            var normalized = terms?.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term.Trim()).ToArray() ?? [];
            if (normalized.Length == 0) throw new ArgumentException("At least one search term is required.");
            limit = Math.Clamp(limit, 1, MaximumSearchLimit);
            var inspected = 0;
            var candidates = cue.Provider.Files.Values.Where(file => normalized.All(term => file.Path.Contains(term, StringComparison.OrdinalIgnoreCase))).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Where(file =>
                {
                    if (assetTypes is not { Length: > 0 }) return true;
                    if (inspected++ >= MaximumContentCandidates) return false;
                    try { return runtime.GetAssetTypes(cue, file).Any(type => assetTypes.Contains(type, StringComparer.OrdinalIgnoreCase)); }
                    catch { return false; }
                }).Take(limit).ToArray();
            return Task.FromResult(JsonConvert.SerializeObject(new { terms = normalized, assetTypes, count = candidates.Length, candidates = candidates.Select(file => new { confidence = 0.75 + Math.Min(0.2, normalized.Length * 0.05), asset = BuildSearchResult(cue, file, true) }), typeInspectionLimited = assetTypes is { Length: > 0 } && inspected >= MaximumContentCandidates }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_export_asset"), Description("Export one asset to an absolute local output directory. kind: raw, properties, textures, models, worlds, animations, audio, or code.")]
    public Task<string> ExportAsset(
        string path,
        string kind,
        string outputDirectory,
        McpExportOptions options = null,
        [Description("Optional folder below outputDirectory. Give each export kind its own subdirectory so a property dump cannot overwrite the richer material description written by a model export.")]
        string subdirectory = null,
        [Description("Return file paths relative to outputDirectory to keep large batches readable.")]
        bool relativePaths = false,
        CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, ct) => ExportAsync(cue, new[] { path }, kind, outputDirectory, options, ct, subdirectory, relativePaths), cancellationToken);

    [McpServerTool(Name = "fmodel_export_batch"), Description("Export up to 25 assets to an absolute local output directory. Reports produced files, newly written files, overwritten files, and per-item failure reasons instead of a bare success flag.")]
    public Task<string> ExportBatch(
        string[] paths,
        string kind,
        string outputDirectory,
        McpExportOptions options = null,
        [Description("Optional folder below outputDirectory. Give each export kind its own subdirectory so a property dump cannot overwrite the richer material description written by a model export.")]
        string subdirectory = null,
        [Description("Return file paths relative to outputDirectory to keep large batches readable.")]
        bool relativePaths = false,
        CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, ct) =>
        {
            if (paths is null || paths.Length == 0) throw new ArgumentException("At least one asset path is required.", nameof(paths));
            if (paths.Length > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(paths), $"A batch may contain at most {MaximumBatchSize} assets.");
            return ExportAsync(cue, paths, kind, outputDirectory, options, ct, subdirectory, relativePaths);
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_render_preview"), Description("Export a texture PNG/WebP or model/world interchange data. 3D screenshot availability depends on the local OpenGL driver.")]
    public Task<string> RenderPreview(string path, string outputDirectory, int width = 1280, int height = 720, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync(async (cue, ct) =>
        {
            var lookup = FModelMcpDiagnostics.ResolveFile(cue, path);
            if (!lookup.Found) return FModelMcpDiagnostics.FromLookup(lookup, "fmodel_render_preview");
            var entry = lookup.Entry;
            var previewKind = ResolvePreviewExportKind(cue, entry);
            var result = await ExportAsync(cue, new[] { entry.Path }, previewKind, outputDirectory, null, ct);
            string screenshot = null;
            string screenshotStatus;
            string screenshotReason = null;
            if (previewKind is "models" or "worlds")
            {
                var fullOutput = ValidateOutputDirectory(outputDirectory);
                var safeName = Regex.Replace(Path.GetFileNameWithoutExtension(entry.Name), "[^a-zA-Z0-9._-]", "_");
                screenshot = Path.Combine(fullOutput, $"{safeName}-{Guid.NewGuid():N}.png");
                var previewSettings = (UserSettings.Default.PreviewStaticMeshes, UserSettings.Default.PreviewSkeletalMeshes, UserSettings.Default.PreviewWorlds, UserSettings.Default.PreviewMaterials);
                FModel.Views.Snooper.Snooper snooper = null;
                try
                {
                    UserSettings.Default.PreviewStaticMeshes = true;
                    UserSettings.Default.PreviewSkeletalMeshes = true;
                    UserSettings.Default.PreviewWorlds = true;
                    UserSettings.Default.PreviewMaterials = true;
                    snooper = cue.SnooperViewer;
                    snooper.PrepareCapture(screenshot, width, height);
                    cue.Extract(ct, entry, false, EBulkType.None);
                    if (!File.Exists(screenshot))
                    {
                        screenshotReason = string.IsNullOrWhiteSpace(snooper.LastCaptureError)
                            ? "FModel did not find a renderable model or world export in this package."
                            : "The OpenGL renderer could not capture this resource.";
                        screenshot = null;
                    }
                }
                catch
                {
                    screenshot = null;
                    screenshotReason = "The local OpenGL renderer is unavailable for this resource.";
                }
                finally
                {
                    snooper?.CancelCapture();
                    (UserSettings.Default.PreviewStaticMeshes, UserSettings.Default.PreviewSkeletalMeshes, UserSettings.Default.PreviewWorlds, UserSettings.Default.PreviewMaterials) = previewSettings;
                }
                screenshotStatus = screenshot == null ? "unavailable" : "created";
            }
            else
            {
                screenshotStatus = "not-applicable";
                screenshotReason = "Texture preview is returned through the exported PNG/WebP files.";
            }
            return JsonConvert.SerializeObject(new
            {
                path = entry.Path,
                previewKind,
                screenshotStatus,
                screenshot,
                screenshotReason,
                export = JsonConvert.DeserializeObject(result)
            }, Formatting.Indented);
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_open_asset"), Description("Open a supported FModel asset headlessly. Returns texture files or a rendered model/world screenshot plus interchange exports.")]
    public Task<string> OpenAsset(string path, string outputDirectory, int width = 1280, int height = 720, CancellationToken cancellationToken = default)
        => RenderPreview(path, outputDirectory, width, height, cancellationToken);

    private static string ResolvePreviewExportKind(CUE4ParseViewModel cue, GameFile entry)
    {
        if (entry.Extension is not ("uasset" or "umap")) return "raw";
        try
        {
            var exportTypes = cue.Provider.LoadPackage(entry.Path).GetExports().Select(x => x.GetType().Name).ToArray();
            if (exportTypes.Any(x => x is "UWorld")) return "worlds";
            if (exportTypes.Any(x => x is "UStaticMesh" or "USkeletalMesh" or "USkeleton")) return "models";
            if (exportTypes.Any(x => x.Contains("Texture", StringComparison.Ordinal))) return "textures";
        }
        catch
        {
            // ExportAsync returns the standard per-asset result if this package cannot be inspected.
        }
        return "textures";
    }

    private static async Task<string> ExportAsync(CUE4ParseViewModel cue, IReadOnlyCollection<string> paths, string kind, string outputDirectory, McpExportOptions options, CancellationToken cancellationToken, string subdirectory = null, bool relativePaths = false)
    {
        var fullOutputDirectory = ValidateOutputDirectory(outputDirectory, subdirectory);
        var bulk = ParseBulkKind(kind);
        var mappingsLoaded = cue.Provider.MappingsContainer != null;

        string Show(string file)
        {
            if (!relativePaths || !Path.IsPathRooted(file)) return file;
            var relative = Path.GetRelativePath(fullOutputDirectory, file);
            return relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
        }

        static DateTime Stamp(string file)
        {
            try { return File.GetLastWriteTimeUtc(file); }
            catch (IOException) { return DateTime.MinValue; }
        }

        var original = CaptureDirectories();
        try
        {
            SetDirectories(fullOutputDirectory);
            var before = Directory.EnumerateFiles(fullOutputDirectory, "*", SearchOption.AllDirectories)
                .GroupBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Stamp(group.First()), StringComparer.OrdinalIgnoreCase);
            var queued = new ExportSession();
            cue.ExportSessionOverride = queued;
            var requests = new List<object>();
            var rejected = new List<object>();

            foreach (var path in paths)
            {
                var lookup = FModelMcpDiagnostics.ResolveFile(cue, path);
                if (!lookup.Found)
                {
                    rejected.Add(new { requestedPath = path, code = lookup.Code, message = lookup.Message, hint = lookup.Hint, candidates = lookup.Candidates });
                    continue;
                }

                try
                {
                    if (bulk is EBulkType.Raw) cue.ExportData(lookup.Entry);
                    else cue.Extract(cancellationToken, lookup.Entry, false, bulk | EBulkType.Auto);
                    requests.Add(new { requestedPath = path, path = lookup.Entry.Path, resolution = lookup.Code });
                }
                catch (Exception exception)
                {
                    rejected.Add(new
                    {
                        requestedPath = path,
                        path = lookup.Entry.Path,
                        code = FModelMcpDiagnostics.Classify(exception, mappingsLoaded),
                        message = FModelMcpDiagnostics.Describe(exception, "The asset could not be queued for export"),
                        hint = FModelMcpDiagnostics.HintFor(FModelMcpDiagnostics.Classify(exception, mappingsLoaded), kind, mappingsLoaded),
                    });
                }
            }

            IReadOnlyList<ExportResult> exportResults = queued.HasQueuedItems
                ? await queued.RunAsync(fullOutputDirectory, options?.Build() ?? UserSettings.GetExportOptions(), ct: cancellationToken)
                : [];

            var succeeded = 0;
            var failed = 0;
            var reported = new List<string>();
            var completed = new List<object>();
            foreach (var result in exportResults)
            {
                if (result.Success) succeeded++;
                else failed++;

                var diskFiles = (result.DiskFilePaths ?? [])
                    .Select(file => Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(fullOutputDirectory, file)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var file in diskFiles)
                    if (!reported.Contains(file, StringComparer.OrdinalIgnoreCase)) reported.Add(file);

                var error = result.Error;
                completed.Add(new
                {
                    path = result.ObjectPath,
                    success = result.Success,
                    code = result.Success || error is null ? null : FModelMcpDiagnostics.Classify(error, mappingsLoaded),
                    error = result.Success ? null : (error is null ? "The exporter reported a failure without an exception." : FModelMcpDiagnostics.Describe(error)),
                    hint = result.Success ? null : FModelMcpDiagnostics.HintFor(FModelMcpDiagnostics.Classify(error, mappingsLoaded), kind, mappingsLoaded),
                    files = diskFiles.Select(Show).ToArray(),
                });
            }

            // ExportResult.DiskFilePaths is only populated by some exporters, so the directory scan is
            // the completeness backstop and `unreportedByExporter` keeps that gap observable. Timestamps
            // are compared against the pre-run value because a rewritten file never appears as "new".
            var after = Directory.EnumerateFiles(fullOutputDirectory, "*", SearchOption.AllDirectories).ToArray();
            var newFiles = after.Where(file => !before.ContainsKey(file)).ToArray();
            var rewritten = after.Where(file => before.TryGetValue(file, out var stamp) && Stamp(file) > stamp).ToArray();

            var produced = new List<string>();
            foreach (var file in newFiles.Concat(rewritten).Concat(reported).Where(file => !string.IsNullOrEmpty(file)))
                if (!produced.Contains(file, StringComparer.OrdinalIgnoreCase)) produced.Add(file);

            var touched = newFiles.Concat(rewritten).ToArray();
            var unreported = touched.Where(file => !reported.Contains(file, StringComparer.OrdinalIgnoreCase)).ToArray();
            var overwritten = rewritten.Concat(reported.Where(before.ContainsKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            return JsonConvert.SerializeObject(new
            {
                kind = bulk.ToString(),
                outputDirectory = fullOutputDirectory,
                ok = rejected.Count == 0 && failed == 0 && requests.Count > 0,
                requested = paths.Count,
                summary = new
                {
                    queued = requests.Count,
                    rejected = rejected.Count,
                    succeeded,
                    failed,
                    producedFiles = produced.Count,
                    newFiles = newFiles.Length,
                    overwrittenFiles = overwritten.Length,
                    unreportedByExporter = unreported.Length,
                },
                queued = requests,
                rejected,
                completed,
                files = produced.Select(Show).ToArray(),
                writtenFiles = newFiles.Select(Show).ToArray(),
                overwrittenFiles = overwritten.Select(Show).ToArray(),
            }, Formatting.Indented);
        }
        finally
        {
            cue.ExportSessionOverride = null;
            RestoreDirectories(original);
        }
    }

    private static EBulkType ParseBulkKind(string kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant() switch
        {
            "models" => "meshes",
            _ => kind
        };
        if (!Enum.TryParse<EBulkType>(normalized, true, out var bulk) || bulk is EBulkType.None or EBulkType.Auto)
            throw new ArgumentException("kind must be one of raw, properties, textures, models, worlds, animations, audio, or code.", nameof(kind));
        return bulk;
    }

    private static string ValidateOutputDirectory(string outputDirectory, string subdirectory = null)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("outputDirectory must be an absolute path.", nameof(outputDirectory));
        var fullPath = Path.GetFullPath(outputDirectory);
        if (!string.IsNullOrWhiteSpace(subdirectory))
        {
            var segments = subdirectory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
                if (segment is "." or ".." || Path.IsPathRooted(segment) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException($"subdirectory must stay inside outputDirectory, but '{subdirectory}' does not.", nameof(subdirectory));
            var combined = Path.GetFullPath(Path.Combine(new[] { fullPath }.Concat(segments).ToArray()));
            var prefix = fullPath.EndsWith(Path.DirectorySeparatorChar.ToString()) ? fullPath : fullPath + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("subdirectory resolved outside outputDirectory.", nameof(subdirectory));
            fullPath = combined;
        }
        try { Directory.CreateDirectory(fullPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException($"outputDirectory cannot be created or written: {exception.Message}", nameof(outputDirectory));
        }
        return fullPath;
    }

    private static DirectorySettings RequireCurrentProfile()
    {
        if (UserSettings.Default.CurrentDir != null) return UserSettings.Default.CurrentDir;
        if (UserSettings.Default.PerDirectory.TryGetValue(UserSettings.Default.GameDirectory, out var profile))
            return UserSettings.Default.CurrentDir = profile;
        throw new InvalidOperationException("No active game profile exists. Call fmodel_configure_game first.");
    }

    private static AesResponse BuildAesResponse(string mainKey, IEnumerable<McpDynamicAesKey> dynamicKeys)
    {
        var response = new AesResponse { MainKey = NormalizeAesKey(mainKey, true), DynamicKeys = [] };
        foreach (var item in dynamicKeys ?? [])
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Guid)) throw new ArgumentException("Every dynamic AES key requires a GUID.");
            var guid = item.Guid.Replace("-", "", StringComparison.Ordinal).Trim();
            if (!Regex.IsMatch(guid, "^[0-9a-fA-F]{32}$")) throw new ArgumentException("A dynamic AES GUID must contain 32 hexadecimal characters.");
            response.DynamicKeys.Add(new DynamicKey { Name = item.Name ?? string.Empty, Guid = guid, Key = NormalizeAesKey(item.Key, false) });
        }
        return response;
    }

    private static string NormalizeAesKey(string key, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            if (allowEmpty) return string.Empty;
            throw new ArgumentException("An AES key is required.");
        }
        var normalized = key.Trim();
        if (!normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) normalized = "0x" + normalized;
        if (!Regex.IsMatch(normalized, "^0x[0-9a-fA-F]{64}$")) throw new ArgumentException("An AES key must contain 64 hexadecimal characters.");
        return normalized;
    }

    private static void ApplyMappings(DirectorySettings profile, string file, string url, string jsonPath)
    {
        if (profile.Endpoints == null || profile.Endpoints.Length <= (int) EEndpointType.Mapping)
            profile.Endpoints = EndpointSettings.Default(profile.GameName);
        var endpoint = profile.Endpoints[(int) EEndpointType.Mapping];
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) throw new FileNotFoundException("The mappings file does not exist.");
            var extension = file.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase) ? ".jmap.gz" : Path.GetExtension(file);
            if (extension is not (".usmap" or ".jmap" or ".jmap.gz")) throw new ArgumentException("mappingsFile must be a .usmap, .jmap, or .jmap.gz file.");
            endpoint.FilePath = Path.GetFullPath(file);
            endpoint.Overwrite = true;
            endpoint.IsValid = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(jsonPath))
            throw new ArgumentException("Provide mappingsFile, or both mappingsUrl and mappingsJsonPath.");
        endpoint.Url = url.Trim();
        endpoint.Path = jsonPath.Trim();
        endpoint.Overwrite = false;
        endpoint.IsValid = true;
    }

    private static string[] CaptureDirectories() => [UserSettings.Default.RawDataDirectory, UserSettings.Default.PropertiesDirectory, UserSettings.Default.TextureDirectory, UserSettings.Default.AudioDirectory, UserSettings.Default.CodeDirectory, UserSettings.Default.ModelDirectory];
    private static void SetDirectories(string value) => (UserSettings.Default.RawDataDirectory, UserSettings.Default.PropertiesDirectory, UserSettings.Default.TextureDirectory, UserSettings.Default.AudioDirectory, UserSettings.Default.CodeDirectory, UserSettings.Default.ModelDirectory) = (value, value, value, value, value, value);
    private static void RestoreDirectories(string[] values) => (UserSettings.Default.RawDataDirectory, UserSettings.Default.PropertiesDirectory, UserSettings.Default.TextureDirectory, UserSettings.Default.AudioDirectory, UserSettings.Default.CodeDirectory, UserSettings.Default.ModelDirectory) = (values[0], values[1], values[2], values[3], values[4], values[5]);

    private object BuildSearchResult(CUE4ParseViewModel cue, GameFile file, bool includeAssetTypes)
    {
        string[] types = null;
        if (includeAssetTypes && file.Extension is "uasset" or "umap")
        {
            try { types = runtime.GetAssetTypes(cue, file); }
            catch { types = []; }
        }
        return new { path = file.Path, extension = file.Extension, size = file.Size, assetTypes = types };
    }

    private object BuildAssetSummary(CUE4ParseViewModel cue, GameFile entry)
    {
        if (entry.Extension is not ("uasset" or "umap")) return new { path = entry.Path, extension = entry.Extension, size = entry.Size, assetTypes = Array.Empty<string>() };
        var exports = cue.Provider.LoadPackage(entry).GetExports().ToArray();
        var serialized = JsonConvert.SerializeObject(exports, Formatting.None);
        var references = Regex.Matches(serialized, @"(?:[A-Za-z0-9_]+/)+(?:Content|Plugins)/[^\""\\]+", RegexOptions.IgnoreCase)
            .Select(match => Regex.Replace(match.Value.Replace('\\', '/'), @"\.\d+$", string.Empty)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
        return new
        {
            path = entry.Path,
            assetTypes = exports.Select(export => export.GetType().Name).Distinct().ToArray(),
            objectNames = exports.Select(export => export.Name).Take(100).ToArray(),
            skeleton = ReadProperty(exports, "Skeleton"),
            materialSlots = ReadProperty(exports, "StaticMaterials", "Materials"),
            lodCount = CountProperty(exports, "LODInfo", "LODs", "ImportedModel"),
            vertexCount = ReadProperty(exports, "NumVertices"),
            triangleCount = ReadProperty(exports, "NumTriangles"),
            references
        };
    }

    private static int ParseCursor(string cursor, int offset)
        => !string.IsNullOrWhiteSpace(cursor) && int.TryParse(cursor, out var parsed) ? Math.Max(0, parsed) : Math.Max(0, offset);

    private static bool MatchesExtension(GameFile file, IEnumerable<string> extensions)
    {
        var values = extensions?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().TrimStart('.')).ToArray() ?? [];
        return values.Length == 0 || values.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesAnyPattern(string path, IEnumerable<string> patterns, bool defaultWhenEmpty)
    {
        var values = patterns?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
        if (values.Length == 0) return defaultWhenEmpty;
        return values.Any(pattern => Regex.IsMatch(path, "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
    }

    private static object ReadProperty(IEnumerable<object> exports, params string[] names)
    {
        foreach (var export in exports)
        foreach (var name in names)
        {
            var property = export.GetType().GetProperty(name);
            if (property?.GetValue(export) is { } value) return FormatValue(value);
        }
        return null;
    }

    private static object FormatValue(object value)
    {
        if (value is string) return value;
        if (value is System.Collections.IEnumerable enumerable)
            return enumerable.Cast<object>().Select(item => item?.ToString()).Where(item => item != null).Take(100).ToArray();
        return value.ToString();
    }

    private static int? CountProperty(IEnumerable<object> exports, params string[] names)
    {
        foreach (var export in exports)
        foreach (var name in names)
        {
            var property = export.GetType().GetProperty(name);
            if (property?.GetValue(export) is System.Collections.ICollection collection) return collection.Count;
            if (property?.GetValue(export) is System.Collections.IEnumerable enumerable) return enumerable.Cast<object>().Count();
        }
        return null;
    }
}
