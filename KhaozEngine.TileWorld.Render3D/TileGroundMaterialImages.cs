using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

// Decodes the two independent image size classes and packs authored maps without changing loader-owned pixels.
internal sealed class TileGroundMaterialImages
{
    sealed record Maps(ImageRgba? Albedo, ImageRgba? Height, ImageRgba? Normal, ImageRgba? Roughness);

    readonly Dictionary<ushort, Maps> _maps = new();
    readonly SizeClass _albedo = new();
    readonly SizeClass _surface = new();

    internal int Width => _albedo.Width == 0 ? 1 : _albedo.Width;
    internal int Height => _albedo.Height == 0 ? 1 : _albedo.Height;
    internal int SurfaceWidth => _surface.Width;
    internal int SurfaceHeight => _surface.Height;

    internal TileGroundMaterialImages(TileWorldCatalogs catalogs, IReadOnlyList<ushort> ids, Func<string, ImageRgba> load)
    {
        foreach (ushort id in ids)
        {
            GroundMaterial material = catalogs.Materials[id];
            ImageRgba? albedo = Decode(material.Texture, "texture", _albedo);
            ImageRgba? height = Decode(material.HeightTexture, "heightTexture", _albedo);
            ImageRgba? normal = Decode(material.NormalTexture, "normalTexture", _surface);
            ImageRgba? roughness = Decode(material.RoughnessTexture, "roughnessTexture", _surface);
            _maps.Add(id, new Maps(albedo, height, normal, roughness));

            ImageRgba? Decode(string? path, string field, SizeClass size)
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                ImageRgba image = load(Resolve(catalogs, material, path, field));
                size.Check(material, image, field);
                return image;
            }
        }
    }

    internal ImageRgba? Albedo(ushort id) => _maps[id].Albedo;

    internal void Apply(ushort id, GroundMaterial material, TileGroundLayerImage layer)
    {
        Maps maps = _maps[id];
        if (maps.Height is { } height)
        {
            layer.AlbedoRgba = (byte[])layer.AlbedoRgba.Clone();
            for (int i = 0; i < layer.AlbedoRgba.Length; i += 4)
                layer.AlbedoRgba[i + 3] = height.Pixels[i + 1];
            layer.HeightMapped = true;
        }
        if (maps.Normal is not null || maps.Roughness is not null)
        {
            var surface = new byte[checked(SurfaceWidth * SurfaceHeight * 4)];
            for (int i = 0; i < surface.Length; i += 4)
            {
                surface[i] = maps.Normal?.Pixels[i] ?? 128;
                surface[i + 1] = maps.Normal?.Pixels[i + 1] ?? 128;
                surface[i + 2] = maps.Normal?.Pixels[i + 2] ?? 255;
                surface[i + 3] = maps.Roughness?.Pixels[i + 1] ?? 128;
            }
            layer.SurfaceRgba = surface;
            layer.NormalMapped = maps.Normal is not null;
            layer.RoughnessMapped = maps.Roughness is not null;
        }
        layer.HeightBlendSharpness = material.HeightBlendSharpness ?? TileGroundRelief.DefaultHeightBlendSharpness;
        layer.FeatherBreakup = material.Feather?.Breakup ?? 0f;
        layer.AntiTiling = material.AntiTiling switch
        {
            GroundMaterialAntiTiling.Offset => TileGroundAntiTiling.Offset,
            GroundMaterialAntiTiling.Rotate => TileGroundAntiTiling.Rotate,
            _ => TileGroundAntiTiling.None,
        };
    }

    // The path a map decodes from. Absolute stays put, relative resolves against the catalog FILE that declared the
    // material, which is the same rule world.json uses for its catalog paths.
    static string Resolve(TileWorldCatalogs catalogs, GroundMaterial material, string path, string field)
    {
        if (Path.IsPathRooted(path)) return path;
        string? source = catalogs.MaterialSource(material.Id);
        // A catalog built in memory has no directory to resolve against, and guessing one (the process working
        // directory, say) would find a different file on every machine.
        if (source is null)
            throw new TileWorldException(TileWorldRefusal.GroundMaterialTextureSourceMissing,
                $"ground material {material.Id} ('{material.Name}') carries relative {field} '{path}', " +
                "but its catalog did not come from a file. Load the catalog from a file or use an absolute path.")
            { MaterialId = material.Id, MaterialName = material.Name, TexturePath = path, Field = field };
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, path));
    }

    sealed class SizeClass
    {
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        ushort _materialId;
        string _materialName = "";

        internal void Check(GroundMaterial material, ImageRgba image, string field)
        {
            if (Width == 0)
            {
                Width = image.Width;
                Height = image.Height;
                _materialId = material.Id;
                _materialName = material.Name;
            }
            else if (image.Width != Width || image.Height != Height)
                throw new TileWorldException(TileWorldRefusal.GroundMaterialTextureSizeMismatch,
                    $"ground material {material.Id} ('{material.Name}') {field} decodes to {image.Width}x{image.Height}, " +
                    $"but its array is {Width}x{Height} from material {_materialId} ('{_materialName}'). " +
                    "Resize the map, the set never resamples it.")
                {
                    MaterialId = material.Id,
                    MaterialName = material.Name,
                    Field = field,
                    Width = image.Width,
                    Height = image.Height,
                    ReferenceMaterialId = _materialId,
                    ReferenceMaterialName = _materialName,
                    ExpectedWidth = Width,
                    ExpectedHeight = Height,
                };
        }
    }
}
