using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One adapter field with its binding and resource figures. Packing attempts are derived from the final
/// bin axes and the one-bin-per-tile start, because the producer does not expose its attempt count.</summary>
internal sealed record TileWaterFieldReport(Vector3 Origin, Vector2 Size, int BinColumns, int BinRows, int BaseColumns,
    int BaseRows, int DerivedPackingAttempts, int Triangles, int EdgeLines, int CandidateReferences, int MaximumBinOccupancy,
    int TextureWidth, int TextureHeight, long TextureBytes, long RetainedBytes, float ReconstructionErrorBoundMetres,
    int Certificates, int SourceOrderMatched, string? SourceOrderError, int ControlAttempts)
{
    internal static TileWaterFieldReport Of(WaterOpticsTriangleField field, Vector3 origin, float tileSize,
        int matched, string? orderError, int controlAttempts)
    {
        int baseColumns = (int)MathF.Ceiling(field.Size.X / tileSize), baseRows = (int)MathF.Ceiling(field.Size.Y / tileSize);
        int attempts = -1;
        for (int k = 0; k <= 10; k++)
            if (Math.Min(1024, baseColumns << k) == field.BinColumns && Math.Min(1024, baseRows << k) == field.BinRows)
            {
                attempts = k + 1;
                break;
            }
        return new(origin, field.Size, field.BinColumns, field.BinRows, baseColumns, baseRows, attempts,
            field.TriangleCount, field.EdgeLineCount, field.CandidateReferenceCount, field.MaximumBinOccupancy,
            field.TextureWidth, field.TextureHeight, field.TextureBytes, field.RetainedBytes,
            field.ReconstructionErrorBoundMetres, field.CertifiedRectangles.Length, matched, orderError, controlAttempts);
    }
}

/// <summary>A GPU rest-depth query against the drawn-mesh oracle and the case's witness surface. Rest depth is
/// relative to the binding's Origin Y, the datum 0 for TileWorld fields, so the oracle is datum minus drawn height.
/// Physical depth is the water surface minus drawn height. Witness is NaN where its surface does not reach.</summary>
internal readonly record struct TileWaterDepthProbe(string Kind, double WorldX, double WorldZ, Vector2 Local, bool Hit,
    float GpuDepth, double OracleDepth, double WitnessDepth, double PhysicalDepth);

/// <summary>Colour influence at one projected water-surface point, against the opaque control.</summary>
internal readonly record struct TileWaterImageProbe(string Name, double WorldX, double WorldZ, Vector2 Pixel,
    double OracleDepth, float AuthoredInfluence, float SceneDepthInfluence, float ControlDistance, float WitnessDistance);

/// <summary>Everything one topology case measured. Capture failures are retained so each fact reports its own case.</summary>
internal sealed class TileWaterTopologyCapture
{
    internal required string Name { get; init; }
    internal Exception? Error { get; set; }
    internal List<TileWaterOpticsFailure> Failures { get; } = new();
    internal int ExpectedFields { get; init; }
    internal int LiveFieldDelta { get; set; }
    internal long UploadDelta { get; set; }
    internal long LiveTextureByteDelta { get; set; }
    internal long LiveRetainedCpuByteDelta { get; set; }
    internal int LiveFieldsAfterRelease { get; set; } = -1;
    internal TileWaterOpticsSourceBytes SourceBytes { get; set; }
    internal List<TileWaterFieldReport> Fields { get; } = new();
    internal List<TileWaterDepthProbe> Depths { get; } = new();
    internal List<TileWaterImageProbe> ImageProbes { get; } = new();
    internal Dictionary<string, WaterOpticsImage> Images { get; } = new();
    internal float ControlMaximumRgb { get; set; } = float.NaN;
    internal float WitnessMaximumRgb { get; set; } = float.NaN;
    internal int DrawnWaterTriangles { get; set; }
    internal Vector3 RenderOrigin { get; set; }
    internal bool RenderOriginActive { get; set; }
    internal int VisibleDeepProbes { get; set; }
    internal int ProjectedDeepProbes { get; set; }
    internal Dictionary<string, double> Milliseconds { get; } = new();

    internal IEnumerable<TileWaterDepthProbe> Admitted => Depths.Where(d => d.Kind != "miss");
    internal double MaximumOracleError => Admitted.Where(d => d.Hit).Select(d => Math.Abs(d.GpuDepth - d.OracleDepth))
        .DefaultIfEmpty(double.NaN).Max();
    internal double MaximumWitnessDeviation => Admitted.Where(d => d.Hit && !double.IsNaN(d.WitnessDepth))
        .Select(d => Math.Abs(d.GpuDepth - d.WitnessDepth)).DefaultIfEmpty(0).Max();

    internal void ThrowIfFailed()
    {
        if (Error is not null)
            throw new InvalidOperationException($"{Name} capture failed. Failures: {DescribeFailures()}", Error);
    }

    internal string DescribeFailures() => Failures.Count == 0 ? "none" : string.Join("; ", Failures.Select(f =>
        $"{f.Kind} region {f.Region} level {f.ReferenceSurfaceY:R} domain {f.Domain} bins {f.BinColumns}x{f.BinRows}"));

    /// <summary>Largest per-pixel RGB distance of two equal-size captures, in normalized units.</summary>
    internal static float MaximumRgbDistance(WaterOpticsImage a, WaterOpticsImage b)
    {
        if (a.Rgba.Length != b.Rgba.Length) throw new ArgumentException("Captures differ in size.");
        float maximum = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
        {
            int r = a.Rgba[i] - b.Rgba[i], g = a.Rgba[i + 1] - b.Rgba[i + 1], blue = a.Rgba[i + 2] - b.Rgba[i + 2];
            maximum = MathF.Max(maximum, MathF.Sqrt(r * r + g * g + blue * blue) / 255f);
        }
        return maximum;
    }

    /// <summary>Largest per-pixel difference of the optical influence, the RGB distance of a water capture from
    /// its own opaque control, between two capture pairs. Terrain colour cancels within each pair.</summary>
    internal static float MaximumInfluenceDifference(WaterOpticsImage a, WaterOpticsImage aOpaque,
        WaterOpticsImage b, WaterOpticsImage bOpaque)
    {
        float maximum = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            maximum = MathF.Max(maximum, MathF.Abs(Distance(a, aOpaque, i) - Distance(b, bOpaque, i)));
        return maximum;
    }

    static float Distance(WaterOpticsImage a, WaterOpticsImage b, int i)
    {
        int r = a.Rgba[i] - b.Rgba[i], g = a.Rgba[i + 1] - b.Rgba[i + 1], blue = a.Rgba[i + 2] - b.Rgba[i + 2];
        return MathF.Sqrt(r * r + g * g + blue * blue) / 255f;
    }

    internal object Metrics(string backend, string device) => new
    {
        Name,
        Backend = backend,
        Device = device,
        Error = Error?.ToString(),
        Failures = Failures.Select(f => new { Kind = f.Kind.ToString(), f.Region, f.ReferenceSurfaceY,
            Domain = f.Domain?.ToString(), f.BinColumns, f.BinRows }).ToArray(),
        ExpectedFields,
        LiveFieldDelta,
        UploadDelta,
        LiveTextureByteDelta,
        LiveRetainedCpuByteDelta,
        LiveFieldsAfterRelease,
        RetainedSourceBytes = new { SourceBytes.Resident, SourceBytes.Pending },
        DrawnWaterTriangles,
        RenderOrigin,
        RenderOriginActive,
        Fields,
        DepthProbes = new
        {
            Count = Depths.Count,
            Admitted = Admitted.Count(),
            Hits = Admitted.Count(d => d.Hit),
            MissProbes = Depths.Count(d => d.Kind == "miss"),
            MissProbeHits = Depths.Count(d => d.Kind == "miss" && d.Hit),
            MaximumOracleError,
            MaximumWitnessDeviation,
            MinimumOracleDepth = Admitted.Select(d => d.OracleDepth).DefaultIfEmpty(double.NaN).Min(),
            MaximumOracleDepth = Admitted.Select(d => d.OracleDepth).DefaultIfEmpty(double.NaN).Max(),
            MinimumPhysicalDepth = Admitted.Select(d => d.PhysicalDepth).DefaultIfEmpty(double.NaN).Min(),
            MaximumPhysicalDepth = Admitted.Select(d => d.PhysicalDepth).DefaultIfEmpty(double.NaN).Max(),
            Probes = Depths,
        },
        Image = new { ControlMaximumRgb, WitnessMaximumRgb, ProjectedDeepProbes, VisibleDeepProbes, Probes = ImageProbes },
        Milliseconds,
    };
}
