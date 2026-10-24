using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>A scene-owned optical field identity. Releasing its slot invalidates its generation immediately.</summary>
public readonly record struct WaterOpticsHandle(Guid Owner, int Index, int Generation)
{
    /// <summary>Whether this identity is well formed. Only its owning scene can establish that it is still live.</summary>
    public bool IsValid => Owner != Guid.Empty && Index >= 0 && Generation > 0;
}

/// <summary>Maps one immutable field to a water body's absolute XZ rectangle and reference surface height.
/// A default binding selects legacy scene-depth water.</summary>
public readonly struct WaterOpticsBinding
{
    /// <summary>Bind a scene-owned field. Origin XZ is the rectangle minimum, Origin Y is the fixed reference
    /// surface, and size is the positive XZ extent. Uses the same absolute frame as the water plane.</summary>
    public WaterOpticsBinding(WaterOpticsHandle field, Vector3 origin, Vector2 size)
    {
        if (!field.IsValid)
            throw new ArgumentException("The optical field handle must be valid.", nameof(field));
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            throw new ArgumentException("The optical field origin must be finite.", nameof(origin));
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0f || size.Y <= 0f)
            throw new ArgumentException("The optical field size must be finite and positive.", nameof(size));
        Field = field;
        Origin = origin;
        Size = size;
    }

    /// <summary>The field's scene-owned GPU identity.</summary>
    public WaterOpticsHandle Field { get; }
    /// <summary>Rectangle minimum in XZ and the source field's reference surface in Y.</summary>
    public Vector3 Origin { get; }
    /// <summary>Positive rectangle extent along X and Z. Triangle payloads require their exact field size.</summary>
    public Vector2 Size { get; }
    /// <summary>Whether this submission opts into independent bed depth.</summary>
    public bool IsBound => Field.IsValid;
}

/// <summary>Optical resource diagnostics. Byte counts cover float32 texture payloads, excluding opaque driver
/// overhead. Retired bytes remain counted until physical disposal. CPU samples remain owned by the retained field.</summary>
public readonly record struct WaterOpticsStats(int LiveFields, long LiveTextureBytes,
    long RetiredTextureBytes, long UploadCount)
{
    /// <summary>Owned immutable live CPU payload bytes, excluding object and array headers.</summary>
    public long LiveRetainedCpuBytes { get; init; }
    /// <summary>Owned immutable CPU payload bytes retained until actual resource retirement.</summary>
    public long RetiredRetainedCpuBytes { get; init; }
}
