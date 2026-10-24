using System;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>One immutable payload kind. Resolution returns the retained value without copying its storage.</summary>
internal readonly struct WaterOpticsPayload
{
    internal WaterOpticsPayload(WaterOpticsField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        BilinearField = field;
    }

    internal WaterOpticsPayload(WaterOpticsTriangleField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        TriangleField = field;
    }

    internal WaterOpticsField? BilinearField { get; }
    internal WaterOpticsTriangleField? TriangleField { get; }
    internal int Mode => TriangleField is null ? 1 : 2;
    internal int Width => TriangleField?.TextureWidth ?? BilinearField!.Width;
    internal int Height => TriangleField?.TextureHeight ?? BilinearField!.Height;
    internal long TextureBytes => TriangleField?.TextureBytes ?? BilinearField!.SampleBytes;
    internal long RetainedBytes => TriangleField?.RetainedBytes ?? BilinearField!.SampleBytes;
    internal ReadOnlySpan<float> Scalars => TriangleField is { } triangles ? triangles.AtlasScalars : BilinearField!.RestDepths;

    internal void ValidateForUpload()
    {
        if (TriangleField is not { } field) return;
        // R32Float image fetches may flush subnormal results on the Vulkan floor. A bitcast after
        // that fetch cannot recover the geometry. Refuse before allocating rather than move a vertex.
        foreach (float scalar in field.AtlasScalars)
        {
            uint magnitude = BitConverter.SingleToUInt32Bits(scalar) & 0x7fffffffu;
            if (magnitude != 0 && magnitude < 0x00800000u)
                throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.UnrepresentableSurface,
                    "A subnormal atlas scalar cannot be preserved by every R32Float image-fetch backend.");
        }
        float inverseX = 1f / field.Size.X, inverseZ = 1f / field.Size.Y;
        if (!float.IsNormal(inverseX) || !float.IsNormal(inverseZ))
            throw new WaterOpticsSurfaceException(WaterOpticsSurfaceFailure.UnrepresentableSurface,
                "The triangle field's inverse extent must be normal and finite for certified shader bin selection.");
    }

    internal void ValidateCoverage(in WaterOpticsBinding binding, double minX, double minZ, double maxX, double maxZ)
    {
        if (TriangleField is not { } field) return;
        if (binding.Size != field.Size)
            throw new ArgumentException("A triangle binding must use the field's exact physical size.", nameof(binding));
        minX -= binding.Origin.X;
        minZ -= binding.Origin.Z;
        maxX -= binding.Origin.X;
        maxZ -= binding.Origin.Z;
        foreach (WaterOpticsRectangle certificate in field.CertifiedRectangles)
            if (minX >= certificate.Minimum.X && minZ >= certificate.Minimum.Y
                && maxX <= certificate.Maximum.X && maxZ <= certificate.Maximum.Y) return;
        throw new ArgumentException("The displaced water rectangle must fit within one immutable triangle certificate.", nameof(binding));
    }
}
