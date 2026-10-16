using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>
/// Every knob of one skinned draw in one value, for
/// <see cref="Scene3D.DrawSkinned(in SkinnedInstanceDraw, ReadOnlySpan{Matrix4x4})"/>. The constructor sets the
/// defaults of the plain <see cref="Scene3D.DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Color)"/>,
/// <see cref="Material.None"/>, no dissolve, casting shadows and no motion key, and a white tint, which that overload
/// takes as an argument instead. The bone palette stays a separate argument because it is a span. Build it with the
/// constructor, because a <c>default</c> value has a transparent tint and casts nothing.
/// </summary>
public readonly struct SkinnedInstanceDraw
{
    /// <summary>A draw of <paramref name="mesh"/> placed by the ABSOLUTE model transform <paramref name="model"/>,
    /// with the plain overload's defaults.</summary>
    public SkinnedInstanceDraw(SkinnedMeshHandle mesh, Matrix4x4 model)
    {
        Mesh = mesh;
        Model = model;
        Tint = Color.White;
        Material = Material.None;
        Dissolve = 0f;
        DissolveEdgeWidth = 0f;
        DissolveEdgeColor = default;
        CastsShadows = true;
        ShadowOnly = false;
        InvertShadowDissolve = false;
        DissolveComplement = 0f;
        Motion = MotionKey.None;
        Coverage = default;
    }

    /// <summary>Optional owned coverage snapshot for this frame.</summary>
    public SkinnedCoverageFrame Coverage { get; init; }

    /// <summary>The already-uploaded skinned mesh.</summary>
    public SkinnedMeshHandle Mesh { get; init; }

    /// <summary>The ABSOLUTE model transform that places, faces and scales the posed mesh.</summary>
    public Matrix4x4 Model { get; init; }

    /// <summary>RGBA multiplied into the lit colour. White by default.</summary>
    public Color Tint { get; init; }

    /// <summary>Emissive glow and specular. <see cref="Material.None"/> by default.</summary>
    public Material Material { get; init; }

    /// <summary>CharDissolve threshold, 0 (solid, the default) to 1 (gone). The shadow erodes with it (issue
    /// #387).</summary>
    public float Dissolve { get; init; }

    /// <summary>Width of the dissolve's glowing edge, a fraction of the noise range.</summary>
    public float DissolveEdgeWidth { get; init; }

    /// <summary>Colour of the dissolve's glowing edge.</summary>
    public Color DissolveEdgeColor { get; init; }

    /// <summary>Whether the draw writes into the key light's depth pass (issue #387). True by default.</summary>
    public bool CastsShadows { get; init; }

    /// <summary>Draw into shadow depth only, with no colour or motion. False by default. Requires
    /// <see cref="CastsShadows"/>, exactly like <see cref="RigidInstanceDraw.ShadowOnly"/>.</summary>
    public bool ShadowOnly { get; init; }

    /// <summary>Invert only the shadow dissolve's dither while dissolving. False by default. Independent of
    /// <see cref="DissolveComplement"/>, exactly like <see cref="RigidInstanceDraw.InvertShadowDissolve"/>.</summary>
    public bool InvertShadowDissolve { get; init; }

    /// <summary>Complement the dissolve coverage in colour and shadow. Zero (the default) keeps the ordinary
    /// coverage, one keeps its inverse, exactly like <see cref="RigidInstanceDraw.DissolveComplement"/>.</summary>
    public float DissolveComplement { get; init; }

    /// <summary>The draw's identity across frames, for temporal effects. <see cref="MotionKey.None"/> (the default)
    /// marks a draw that reports camera-only motion. See <see cref="MotionKey"/>.</summary>
    public MotionKey Motion { get; init; }
}
