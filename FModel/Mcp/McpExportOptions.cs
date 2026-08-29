using System;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using FModel.Settings;

namespace FModel.Mcp;

public sealed class McpExportOptions
{
    public string MeshFormat { get; set; }
    public string NaniteMeshFormat { get; set; }
    public string MeshQuality { get; set; }
    public string TexturePlatform { get; set; }
    public string TextureFormat { get; set; }
    public int? TextureQuality { get; set; }
    public bool? ExportHdrTexturesAsHdr { get; set; }
    public string MaterialDepth { get; set; }
    public bool? ExportMaterials { get; set; }
    public bool? ExportMorphTargets { get; set; }
    public string SocketFormat { get; set; }
    public string CompressionFormat { get; set; }
    public bool? ExportAllTextureMips { get; set; }

    public ExportOptions Build()
    {
        var defaults = UserSettings.GetExportOptions();
        return new ExportOptions(
            Parse(MeshFormat, defaults.MeshFormat),
            Parse(NaniteMeshFormat, defaults.NaniteMeshFormat),
            Parse(MeshQuality, defaults.MeshQuality),
            Parse(TexturePlatform, defaults.TexturePlatform),
            Parse(TextureFormat, defaults.TextureFormat),
            Math.Clamp(TextureQuality ?? defaults.TextureQuality, 1, 100),
            ExportHdrTexturesAsHdr ?? defaults.ExportHdrTexturesAsHdr,
            Parse(MaterialDepth, defaults.MaterialDepth),
            ExportMaterials ?? defaults.ExportMaterials,
            ExportMorphTargets ?? defaults.ExportMorphTargets,
            Parse(SocketFormat, defaults.SocketFormat),
            Parse(CompressionFormat, defaults.CompressionFormat),
            ExportAllTextureMips ?? defaults.ExportAllTextureMips);
    }

    private static T Parse<T>(string value, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (Enum.TryParse<T>(value, true, out var parsed)) return parsed;
        throw new ArgumentException($"Invalid {typeof(T).Name} value. Allowed values: {string.Join(", ", Enum.GetNames<T>())}.");
    }
}
