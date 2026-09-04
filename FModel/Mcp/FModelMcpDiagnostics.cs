using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider.Objects;
using FModel.ViewModels;
using Newtonsoft.Json;

namespace FModel.Mcp;

/// <summary>
/// Agent-readable diagnostics. The MCP transport collapses thrown tool exceptions into a fixed
/// "An error occurred invoking 'x'." notice, so expected failures must be returned as data
/// rather than thrown, and every failure needs a code, a cause, and a next action.
/// </summary>
public static class FModelMcpDiagnostics
{
    private static readonly string[] AssetExtensions = ["uasset", "umap", "uexp", "ubulk", "uptl", "ini", "json", "csv", "txt", "locres"];

    public sealed record FileLookup(GameFile Entry, string RequestedPath, string Code, string Message, string Hint, string[] Candidates)
    {
        public bool Found => Entry is not null;
    }

    /// <summary>Resolve a mounted file, tolerating the extension mismatch that trips most callers.</summary>
    public static FileLookup ResolveFile(CUE4ParseViewModel cue, string path)
    {
        var files = cue.Provider.Files;
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();

        if (normalized.Length == 0)
            return new FileLookup(null, path ?? string.Empty, "empty_path",
                "No asset path was supplied.",
                "Pass a mounted path as fmodel_search_assets or fmodel_list_directory returns it, for example 'Project/Content/Dir/Asset.uasset'.",
                []);

        if (files.TryGetValue(normalized, out var exact))
            return new FileLookup(exact, path, "ok", "Resolved by exact path.", null, []);

        var lastDot = normalized.LastIndexOf('.');
        var stem = lastDot < 0 ? normalized : normalized[..lastDot];
        var tail = lastDot < 0 ? string.Empty : normalized[(lastDot + 1)..];
        var hasKnownExtension = lastDot >= 0 && AssetExtensions.Contains(tail, StringComparer.OrdinalIgnoreCase);

        var attempts = new List<string>();
        if (hasKnownExtension) attempts.Add(stem);
        else foreach (var ext in new[] { "uasset", "umap" }) attempts.Add(normalized + "." + ext);

        foreach (var attempt in attempts)
        {
            if (!files.TryGetValue(attempt, out var found)) continue;
            var companionHint = found.Extension is "uasset" or "umap"
                ? null
                : "'." + found.Extension + "' is a companion file, not an addressable asset package. Export or inspect the '.uasset' path instead.";
            return new FileLookup(found, path, "resolved_with_normalization",
                $"'{normalized}' is not mounted; '{found.Path}' was resolved instead.",
                companionHint ?? "Use the returned path verbatim on subsequent calls.",
                []);
        }

        var name = Path.GetFileNameWithoutExtension(stem);
        var candidates = string.IsNullOrEmpty(name)
            ? []
            : files.Values
                .Where(file => Path.GetFileNameWithoutExtension(file.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Path.Length)
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();

        if (candidates.Length > 0)
            return new FileLookup(null, path, "name_matches_but_path_differs",
                $"'{normalized}' is not mounted, but {candidates.Length} mounted file(s) are named '{name}'.",
                "The folder is most likely wrong. Retry with one of candidates, or call fmodel_find_asset with the bare asset name.",
                candidates);

        return new FileLookup(null, path, "asset_not_mounted",
            $"'{normalized}' is not present in any mounted archive.",
            "Check that the intended project is the active profile (fmodel_status) and that its archives are mounted, then locate the asset with fmodel_find_asset or fmodel_list_directory.",
            []);
    }

    /// <summary>Describe a caught exception the way an agent can act on it: type, message, and cause chain.</summary>
    public static string Describe(Exception exception, string? context = null)
    {
        var current = exception;
        var chain = new List<string>();
        var depth = 0;
        while (current is not null && depth++ < 4)
        {
            chain.Add(current.GetType().Name + ": " + current.Message);
            current = current is AggregateException aggregate ? (aggregate.InnerException ?? current.InnerException) : current.InnerException;
        }
        var message = context is null ? string.Join(" <- ", chain) : context + " (" + string.Join(" <- ", chain) + ")";
        return message;
    }

    public static string Classify(Exception exception, bool mappingsLoaded)
    {
        var root = exception;
        while (root.InnerException is not null) root = root.InnerException;
        return root switch
        {
            FileNotFoundException => "asset_not_found",
            DirectoryNotFoundException => "directory_not_found",
            UnauthorizedAccessException => "access_denied",
            OperationCanceledException => "cancelled",
            NotSupportedException => "unsupported_option",
            DllNotFoundException => "native_library_missing",
            FormatException => "decode_failed",
            _ when !mappingsLoaded => "decode_incomplete_possibly_missing_mappings",
            _ => "export_failed",
        };
    }

    /// <summary>Pick the advice that actually matches the failure. A specific cause must not be
    /// dressed up as a mappings problem, or the caller chases the wrong fix.</summary>
    public static string? HintFor(string code, string kind, bool mappingsLoaded) => code switch
    {
        "unsupported_option" => null,
        "native_library_missing" =>
            "A native dependency is absent. CUE4Parse-Natives is compiled by CMake during the build and is skipped without failing the build when CMake or a C++ toolchain is missing, so a published binary can silently lack ACL-compressed animation support. Check nativesLoaded from fmodel_status; when it is false, kind=animations cannot succeed for any format.",
        "cancelled" => null,
        "asset_not_found" or "asset_not_mounted" or "name_matches_but_path_differs" =>
            "Locate the asset with fmodel_find_asset or fmodel_list_directory, then retry with an exact mounted path including its .uasset extension.",
        "access_denied" or "directory_not_found" =>
            "Check that the output path exists and is writable by the account running the MCP server.",
        "decode_failed" => "The package decoded far enough to identify the asset but not far enough to convert it; confirm the game version and profile in fmodel_status.",
        _ => MappingsHint(kind, mappingsLoaded),
    };

    /// <summary>
    /// Capability gaps that silently degrade results instead of raising an error. Read this before
    /// concluding that a successful export is complete.
    /// </summary>
    public static string[] ActiveLimitations(CUE4ParseViewModel cue)
    {
        var notes = new List<string>();
        if (cue.Provider.MappingsContainer is null)
            notes.Add("mappings_not_loaded: UE5 serialized properties, skeletons, and material parameters may decode only partially");
        if (!CUE4Parse.Utils.CUE4ParseNatives.IsInitialized)
            notes.Add("natives_not_loaded: CUE4Parse-Natives is absent, so ACL-compressed animation tracks cannot be decoded. kind=animations will fail and property dumps show only frame counts.");
        if (cue.Provider.UnloadedVfs.Count > 0)
            notes.Add("archives_pending: " + cue.Provider.UnloadedVfs.Count + " archive(s) are still mounting; search and export results may be incomplete.");
        return [.. notes];
    }


    public static string? MappingsHint(string kind, bool mappingsLoaded)
    {
        if (mappingsLoaded) return null;
        return kind?.Trim().ToLowerInvariant() switch
        {
            "animations" or "meshes" or "models" or "worlds" or "code" =>
                "Mappings are not loaded for this profile. UE5 serialized properties, skeletons, and ACL-compressed animation tracks decode only partially without a .usmap/.jmap file; an exporter failure here is usually mappings, not the asset. Configure one with fmodel_set_mappings and restart the server.",
            _ => "Mappings are not loaded for this profile, so UE5 property payloads may decode incompletely even when the export reports success.",
        };
    }

    public static string Error(string code, string message, string? hint = null, IReadOnlyList<string>? candidates = null, IDictionary<string, object?>? extra = null)
    {
        var error = new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        if (!string.IsNullOrWhiteSpace(hint)) error["hint"] = hint;
        if (candidates is { Count: > 0 }) error["candidates"] = candidates;
        var root = new Dictionary<string, object?> { ["ok"] = false, ["error"] = error };
        if (extra is not null) foreach (var pair in extra) root[pair.Key] = pair.Value;
        return JsonConvert.SerializeObject(root, Formatting.Indented);
    }

    public static string FromLookup(FileLookup lookup, string? context = null)
    {
        var extra = new Dictionary<string, object?> { ["requestedPath"] = lookup.RequestedPath };
        if (context is not null) extra["context"] = context;
        return Error(lookup.Code, lookup.Message, lookup.Hint, lookup.Candidates, extra);
    }
}