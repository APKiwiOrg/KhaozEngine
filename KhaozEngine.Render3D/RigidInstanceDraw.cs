using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>
/// Every knob of one rigid instance draw in one value, for <see cref="Scene3D.Draw(in RigidInstanceDraw)"/>
/// (TEMPORAL-FOUNDATIONS-DESIGN section 3). The constructor sets the defaults of the plain
/// <see cref="Scene3D.Draw(MeshHandle, Matrix4x4)"/>: white tint, <see cref="Material.None"/>, no dissolve, casting
/// shadows, visible, no motion key. Set the rest with an object initializer. Every <c>Draw(MeshHandle, ...)</c>
/// overload builds one of these and forwards, so both routes queue exactly the same instance. A future draw knob
/// becomes a property here rather than another overload.
/// <code>
/// scene.Draw(new RigidInstanceDraw(sword, handWorld) { Tint = steel, Motion = MotionKey.Combine(body, 1) });
/// </code>
/// Build it with the constructor. A <c>default</c> value has a transparent tint, a zero-shininess material and no
/// shadow casting, which is not the plain draw.
/// </summary>
public readonly struct RigidInstanceDraw
{
    /// <summary>A draw of <paramref name="mesh"/> at the ABSOLUTE world transform <paramref name="world"/>, with the
    /// plain overload's defaults.</summary>
    public RigidInstanceDraw(MeshHandle mesh, Matrix4x4 world)
    {
        Mesh = mesh;
        World = world;
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
    }

    /// <summary>The already-uploaded mesh.</summary>
    public MeshHandle Mesh { get; init; }

    /// <summary>The ABSOLUTE world transform. The scene reduces it against the render origin itself.</summary>
    public Matrix4x4 World { get; init; }

    /// <summary>RGBA multiplied into the lit colour. White by default.</summary>
    public Color Tint { get; init; }

    /// <summary>Emissive glow and specular. <see cref="Material.None"/> by default.</summary>
    public Material Material { get; init; }

    /// <summary>Rigid dissolve threshold (issue #253), 0 (solid, the default) to 1 (gone).</summary>
    public float Dissolve { get; init; }

    /// <summary>Width of the dissolve's glowing edge, a fraction of the noise range.</summary>
    public float DissolveEdgeWidth { get; init; }

    /// <summary>Colour of the dissolve's glowing edge.</summary>
    public Color DissolveEdgeColor { get; init; }

    /// <summary>Whether the instance writes into the key light's shadow depth pass (issue #287). True by default.
    /// False draws and receives shadows but casts none.</summary>
    public bool CastsShadows { get; init; }

    /// <summary>Draw into the shadow depth pass alone and never in the colour pass (issue #974). Needs
    /// <see cref="CastsShadows"/>: shadow-only without casting draws nowhere and is refused when queued. A shadow-only
    /// draw is never seen, so its <see cref="Motion"/> is ignored.</summary>
    public bool ShadowOnly { get; init; }

    /// <summary>Invert the SHADOW dither of a dissolving instance, for the merged half of an HLOD crossfade (issue
    /// #391). The colour pass is unchanged.</summary>
    public bool InvertShadowDissolve { get; init; }

    /// <summary>Complementary dissolve phase for a LOD handoff. 0 keeps the ordinary noise ownership, 1 keeps its
    /// exact complement in both the colour and shadow passes.</summary>
    public float DissolveComplement { get; init; }

    /// <summary>The draw's identity across frames, for temporal effects. <see cref="MotionKey.None"/> (the default)
    /// marks a static draw. See <see cref="MotionKey"/>.</summary>
    public MotionKey Motion { get; init; }
}
