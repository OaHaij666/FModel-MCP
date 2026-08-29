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
using FModel.Extensions;
using FModel.Settings;
using FModel.ViewModels;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

namespace FModel.Mcp;

[McpServerToolType]
public sealed class FModelMcpTools(FModelMcpRuntime runtime)
{
    private const int DefaultSearchLimit = 100;
    private const int MaximumSearchLimit = 1_000;
    private const int MaximumBatchSize = 25;

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
            configuredKeyCount = cue.Provider.Keys.Count
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
    public Task<string> ExportAsset(string path, string kind, string outputDirectory, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, ct) => ExportAsync(cue, new[] { path }, kind, outputDirectory, ct), cancellationToken);

    [McpServerTool(Name = "fmodel_export_batch"), Description("Export up to 25 assets to an absolute local output directory. Each item reports its result.")]
    public Task<string> ExportBatch(string[] paths, string kind, string outputDirectory, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync((cue, ct) =>
        {
            if (paths is null || paths.Length == 0) throw new ArgumentException("At least one asset path is required.", nameof(paths));
            if (paths.Length > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(paths), $"A batch may contain at most {MaximumBatchSize} assets.");
            return ExportAsync(cue, paths, kind, outputDirectory, ct);
        }, cancellationToken);

    [McpServerTool(Name = "fmodel_render_preview"), Description("Export a texture PNG/WebP or model/world interchange data. 3D screenshot availability depends on the local OpenGL driver.")]
    public Task<string> RenderPreview(string path, string outputDirectory, CancellationToken cancellationToken = default)
        => runtime.RunExclusiveAsync(async (cue, ct) =>
        {
            var entry = FModelMcpRuntime.GetFile(cue, path);
            var previewKind = ResolvePreviewExportKind(cue, entry);
            var result = await ExportAsync(cue, new[] { path }, previewKind, outputDirectory, ct);
            return JsonConvert.SerializeObject(new
            {
                path = entry.Path,
                previewKind,
                screenshot = "unavailable",
                screenshotReason = previewKind is "models" or "worlds"
                    ? "The OpenGL renderer is unavailable for this headless request; the model/world interchange export remains available."
                    : "Texture preview is provided by FModel's PNG/WebP exporter.",
                export = JsonConvert.DeserializeObject(result)
            }, Formatting.Indented);
        }, cancellationToken);

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

    private static async Task<string> ExportAsync(CUE4ParseViewModel cue, IReadOnlyCollection<string> paths, string kind, string outputDirectory, CancellationToken cancellationToken)
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
                ? await queued.RunAsync(fullOutputDirectory, UserSettings.GetExportOptions(), ct: cancellationToken)
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

    private static string[] CaptureDirectories() => [UserSettings.Default.RawDataDirectory, UserSettings.Default.PropertiesDirectory, UserSettings.Default.TextureDirectory, UserSettings.Default.AudioDirectory, UserSettings.Default.CodeDirectory, UserSettings.Default.ModelDirectory];
    private static void SetDirectories(string value) => (UserSettings.Default.RawDataDirectory, UserSettings.Default.PropertiesDirectory, UserSettings.Default.TextureDirectory, UserSettings.Default.AudioDirectory, UserSettings.Default.CodeDirectory, UserSettings.Default.ModelDirectory) = (value, value, value, value, value, value);
    private static void RestoreDirectories(string[] values) => (UserSettings.Default.RawDataDirectory, UserSettings.Default.PropertiesDirectory, UserSettings.Default.TextureDirectory, UserSettings.Default.AudioDirectory, UserSettings.Default.CodeDirectory, UserSettings.Default.ModelDirectory) = (values[0], values[1], values[2], values[3], values[4], values[5]);
}
