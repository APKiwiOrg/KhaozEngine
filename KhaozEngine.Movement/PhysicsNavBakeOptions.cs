using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Absolute half-open XZ bounds and bounded storage controls for a static physics capture.
/// Probe height is absolute Y. Rebase physics to small local coordinates before capture.
/// Cell and edge budgets bound stored data and later traversal probes, not column-query elapsed time.</summary>
public sealed record PhysicsNavBakeOptions(
    float MinX, float MinZ, float MaxX, float MaxZ, float CellSize,
    float ProbeHeight, float ProbeRange, float MaxSlopeRadians,
    int MaxCells, int MaxLayerCells, int MaxSurfacesPerColumn = 4,
    float EdgeProbeSeconds = 1f / 30f, int MaxEdgeProbeSteps = 64)
{
    internal (int Width, int Height, int Cells, int Samples) Validate(Vector3 origin)
    {
        ValidateFields();
        Finite(MinX - origin.X, nameof(MinX));
        Finite(MaxX - origin.X, nameof(MaxX));
        Finite(MinZ - origin.Z, nameof(MinZ));
        Finite(MaxZ - origin.Z, nameof(MaxZ));
        Finite(ProbeHeight - origin.Y, nameof(ProbeHeight));
        Finite(ProbeHeight - ProbeRange, nameof(ProbeRange));
        Finite(ProbeHeight - origin.Y - ProbeRange, nameof(ProbeRange));
        return Layout();
    }

    /// <summary>The checks of <see cref="Validate"/> that do not depend on a physics origin, for a loader that
    /// has no physics world. Throws <see cref="ArgumentOutOfRangeException"/> as <see cref="Validate"/> does.</summary>
    internal (int Width, int Height, int Cells, int Samples) ValidateWithoutOrigin()
    {
        ValidateFields();
        Finite(ProbeHeight - ProbeRange, nameof(ProbeRange));
        return Layout();
    }

    private void ValidateFields()
    {
        Finite(MinX, nameof(MinX));
        Finite(MinZ, nameof(MinZ));
        Finite(MaxX, nameof(MaxX));
        Finite(MaxZ, nameof(MaxZ));
        if (MaxX <= MinX) throw new ArgumentOutOfRangeException(nameof(MaxX));
        if (MaxZ <= MinZ) throw new ArgumentOutOfRangeException(nameof(MaxZ));
        Positive(CellSize, nameof(CellSize));
        Finite(ProbeHeight, nameof(ProbeHeight));
        Positive(ProbeRange, nameof(ProbeRange));
        if (!float.IsFinite(MaxSlopeRadians) || MaxSlopeRadians < 0f || MaxSlopeRadians >= MathF.PI / 2f)
            throw new ArgumentOutOfRangeException(nameof(MaxSlopeRadians));
        if (MaxCells < 1) throw new ArgumentOutOfRangeException(nameof(MaxCells));
        if (MaxLayerCells < 1) throw new ArgumentOutOfRangeException(nameof(MaxLayerCells));
        if (MaxSurfacesPerColumn < 1) throw new ArgumentOutOfRangeException(nameof(MaxSurfacesPerColumn));
        Positive(EdgeProbeSeconds, nameof(EdgeProbeSeconds));
        if (MaxEdgeProbeSteps < 1 || !float.IsFinite(EdgeProbeSeconds * MaxEdgeProbeSteps))
            throw new ArgumentOutOfRangeException(nameof(MaxEdgeProbeSteps));
    }

    private (int Width, int Height, int Cells, int Samples) Layout()
    {
        int width, height, cells, samples;
        try
        {
            // Match the grounded layered bake's float division before ceiling.
            width = checked((int)MathF.Ceiling((MaxX - MinX) / CellSize));
            height = checked((int)MathF.Ceiling((MaxZ - MinZ) / CellSize));
            cells = checked(width * height);
            samples = checked(cells * MaxSurfacesPerColumn);
            int prefixLength = checked(cells + 1);
            int probeCapacity = checked(MaxSurfacesPerColumn + 1);
            _ = checked(cells * probeCapacity);
            if (width < 1 || height < 1 || prefixLength > Array.MaxLength ||
                samples > Array.MaxLength || probeCapacity > Array.MaxLength)
                throw new OverflowException();
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(CellSize), "Capture dimensions or sample buffers exceed supported array sizes.");
        }
        if (cells > MaxCells) throw new ArgumentOutOfRangeException(nameof(MaxCells), "Capture exceeds the cell budget.");
        if (cells > MaxLayerCells)
            throw new ArgumentOutOfRangeException(nameof(MaxLayerCells), "Even one layer exceeds the layer-cell budget.");
        Finite(MinX + (width - 0.5f) * CellSize, nameof(MaxX));
        Finite(MinZ + (height - 0.5f) * CellSize, nameof(MaxZ));
        return (width, height, cells, samples);
    }

    private static void Finite(float value, string control)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(control, "Value must be finite.");
    }

    private static void Positive(float value, string control)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(control, "Value must be finite and positive.");
    }
}
