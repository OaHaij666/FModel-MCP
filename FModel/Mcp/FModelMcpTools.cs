using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

namespace FModel.Mcp;

[McpServerToolType]
public sealed class FModelMcpTools(FModelMcpRuntime runtime)
{
    private const int DefaultSearchLimit = 100;
    private const int MaximumSearchLimit = 1_000;
    private const int MaximumBatchSize = 25;

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

    [McpServerTool(Name = "fmodel_status"), Description("Report FModel archive mounting and readiness without exposing game paths or keys.")]
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
            mappingsLoaded = cue.Provider.MappingsContainer != null
        }, Formatting.Indented)), cancellationToken);

    [McpServerTool(Name = "fmodel_search_assets"), Description("Search mounted FModel asset paths. Results are capped at 1000 entries.")]
    public Task<string> SearchAssets(
        [Description("Text or regular expression to match against asset paths.")] string query,
        [Description("Treat query as a .NET regular expression.")] bool regex = false,
        [Description("Use case-sensitive matching.")] bool caseSensitive = false,
        [Description("Maximum result count, from 1 through 1000.")] int limit = DefaultSearchLimit,
        CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.", nameof(query));
            limit = Math.Clamp(limit, 1, MaximumSearchLimit);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Regex? matcher = regex ? new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)) : null;
            var results = cue.Provider.Files.Values
                .Where(file => matcher?.IsMatch(file.Path) ?? file.Path.Contains(query, comparison))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(file => new { path = file.Path, extension = file.Extension, size = file.Size })
                .ToArray();
            return Task.FromResult(JsonConvert.SerializeObject(new { query, regex, count = results.Length, results }, Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_get_asset_metadata"), Description("Return FModel's structured metadata for a mounted asset path.")]
    public Task<string> GetAssetMetadata([Description("Exact mounted asset path.")] string path, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, _) =>
        {
            var entry = FModelMcpRuntime.GetFile(cue, path);
            if (entry.Extension is not ("uasset" or "umap"))
                return Task.FromResult(JsonConvert.SerializeObject(new { path = entry.Path, extension = entry.Extension, size = entry.Size }, Formatting.Indented));
            var result = cue.Provider.GetLoadPackageResult(entry);
            return Task.FromResult(JsonConvert.SerializeObject(result.GetDisplayData(false), Formatting.Indented));
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_export_asset"), Description("Export one asset to an absolute local output directory. kind: raw, properties, textures, models, worlds, animations, audio, or code.")]
    public Task<string> ExportAsset(string path, string kind, string outputDirectory, McpExportOptions options = null, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, ct) => ExportAsync(cue, new[] { path }, kind, outputDirectory, options, ct), cancellationToken);

    [McpServerTool(Name = "fmodel_export_batch"), Description("Export up to 25 assets to an absolute local output directory. Each item reports its result.")]
    public Task<string> ExportBatch(string[] paths, string kind, string outputDirectory, McpExportOptions options = null, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, ct) =>
        {
            if (paths is null || paths.Length == 0) throw new ArgumentException("At least one asset path is required.", nameof(paths));
            if (paths.Length > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(paths), $"A batch may contain at most {MaximumBatchSize} assets.");
            return ExportAsync(cue, paths, kind, outputDirectory, options, ct);
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_render_preview"), Description("Export a texture PNG/WebP or model/world interchange data. 3D screenshot availability depends on the local OpenGL driver.")]
    public Task<string> RenderPreview(string path, string outputDirectory, int width = 1280, int height = 720, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync(async (cue, ct) =>
        {
            var entry = FModelMcpRuntime.GetFile(cue, path);
            var previewKind = ResolvePreviewExportKind(cue, entry);
            var result = await ExportAsync(cue, new[] { path }, previewKind, outputDirectory, null, ct);
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

    private static async Task<string> ExportAsync(CUE4ParseViewModel cue, IReadOnlyCollection<string> paths, string kind, string outputDirectory, McpExportOptions options, CancellationToken cancellationToken)
    {
        var fullOutputDirectory = ValidateOutputDirectory(outputDirectory);
        var bulk = ParseBulkKind(kind);

        var original = CaptureDirectories();
        try
        {
            SetDirectories(fullOutputDirectory);
            var before = Directory.EnumerateFiles(fullOutputDirectory, "*", SearchOption.AllDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var queued = new ExportSession();
            cue.ExportSessionOverride = queued;
            var results = new List<object>();
            foreach (var path in paths)
            {
                try
                {
                    var entry = FModelMcpRuntime.GetFile(cue, path);
                    if (bulk is EBulkType.Raw)
                        cue.ExportData(entry);
                    else
                        cue.Extract(cancellationToken, entry, false, bulk | EBulkType.Auto);
                    results.Add(new { path = entry.Path, queued = true });
                }
                catch (Exception)
                {
                    results.Add(new { path, success = false, error = "The asset could not be prepared for export." });
                }
            }
            IReadOnlyList<ExportResult> exportResults = queued.HasQueuedItems
                ? await queued.RunAsync(fullOutputDirectory, options?.Build() ?? UserSettings.GetExportOptions(), ct: cancellationToken)
                : [];
            var completed = exportResults.Select(x => new { path = x.ObjectPath, success = x.Success, error = x.Success ? null : "The exporter reported a failure." }).ToArray();
            var writtenFiles = Directory.EnumerateFiles(fullOutputDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !before.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return JsonConvert.SerializeObject(new { kind = bulk.ToString(), outputDirectory = fullOutputDirectory, writtenFiles, queued = results, completed }, Formatting.Indented);
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

    private static string ValidateOutputDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("outputDirectory must be an absolute path.", nameof(outputDirectory));
        var fullPath = Path.GetFullPath(outputDirectory);
        try { Directory.CreateDirectory(fullPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException("outputDirectory cannot be created or written.", nameof(outputDirectory));
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
}
