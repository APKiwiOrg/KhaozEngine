using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Immutable synthetic river inputs. Extra controls isolate transport and age resource layouts.</summary>
public sealed record WaterColumnCase
{
    public float Depth { get; init; } = 0.3f;
    public float Clarity { get; init; } = 2f;
    public float SunElevationDegrees { get; init; } = 35f;
    public float ShadowStrength { get; init; } = 1f;
    public float GlintStrength { get; init; }
    public float FoamStrength { get; init; }
    public float CasterTop { get; init; } = 0.6f;
    public float CasterBottom { get; init; } = -0.3f;
    public Color WaterColor { get; init; } = new(0.10f, 0.13f, 0.10f);
    public bool Enabled { get; init; } = true;
    public bool Caster { get; init; } = true;
    public bool Ripples { get; init; } = true;
    public bool TopDown { get; init; }
    public bool Orthographic { get; init; }
    public bool BlackBed { get; init; }
    public bool BedOnly { get; init; }
    public bool Bound { get; init; }
    public bool Atmosphere { get; init; }
    public bool ReferenceLight { get; init; }
    public AntiAliasing Aa { get; init; }
    public WaterWaveSource WaveSource { get; init; } = WaterWaveSource.Procedural;
    public float? AuthoredDepth { get; init; }
    public Vector3? Eye { get; init; }
    public Vector3 Origin { get; init; }
    public bool AmbientOnly { get; init; }
    public bool DryReference { get; init; }
    public bool NullColumnFields { get; init; }
    public bool MissingBed { get; init; }
    public bool MultiplePlanes { get; init; }
    public bool Hdr { get; init; } = true;
    public int Size { get; init; } = WaterColumnRiverScene.Size;
    public int ShadowResolution { get; init; } = 2048;
    public int ShadowCascades { get; init; } = 1;
}
