using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Landing 1 acceptance. Every Golden is an absolute assertion, with no baked snapshot.</summary>
public sealed class WaterColumnGpuTests(WaterColumnRiverScene scene, ITestOutputHelper output)
    : IClassFixture<WaterColumnRiverScene>
{
    // At 35 degrees the box's 0.6 m air height extends its shadow about 0.857 m toward +Z.
    internal static readonly Vector3 ShadowColumn = new(0f, 0f, 0.55f);
    internal static readonly Vector3 LitColumn = new(1.5f, 0f, 0.55f);
    internal static float L(Vector3 rgb) => Vector3.Dot(rgb, new Vector3(0.2126f, 0.7152f, 0.0722f));
    internal static float MaxChannelAbs(Vector3 value) => MathF.Max(MathF.Abs(value.X),
        MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));

    // Ignoring the sun-entry shadow would leave the adjacent column bright and detach the dark component.
    [GpuFact]
    public void Golden_ColumnShadowTouchesFloaterWaterline()
    {
        var settings = new WaterColumnCase();
        WaterColumnImage shadow = scene.Capture(settings);
        WaterColumnImage open = scene.Capture(settings with { Caster = false });
        Vector3 shadowColumn = shadow.Sample(ShadowColumn), litColumn = shadow.Sample(LitColumn);
        float positiveControl = L(open.Sample(ShadowColumn)) - L(shadowColumn);
        float distance = AttachedBoundaryDistance(shadow, open);
        output.WriteLine($"column margin={L(litColumn) - L(shadowColumn):F6}, caster-free margin={positiveControl:F6}, boundary distance={distance:F3} px");
        Assert.True(L(litColumn) - L(shadowColumn) >= 0.025f);
        Assert.True(positiveControl >= 0.025f);
        Assert.True(distance <= 2f);

        // A black source and fixed optical depth remove the opaque box and its shallower scene depth as causes.
        var columnOnly = settings with { BlackBed = true, Bound = true };
        WaterColumnImage pure = scene.Capture(columnOnly), pureOpen = scene.Capture(columnOnly with { Caster = false });
        float pureMargin = L(pure.Sample(LitColumn) - pure.Sample(ShadowColumn));
        output.WriteLine($"isolated column margin={pureMargin:F6}");
        Assert.True(pureMargin >= 0.025f);
        Assert.True(L(pureOpen.Sample(ShadowColumn) - pure.Sample(ShadowColumn)) >= 0.025f);
        Assert.True(AttachedBoundaryDistance(pure, pureOpen) <= 2f);
    }

    // Sand minus black cancels column in-scatter. A non-fading bed or missing extinction fails the ordering.
    [GpuFact]
    public void Golden_BedShadowSignalFadesWithDepth()
    {
        var contrasts = new List<float>();
        foreach (float depth in new[] { 0.05f, 0.30f, 1.0f, 2.0f })
        {
            var settings = new WaterColumnCase { Depth = depth, TopDown = true, Orthographic = true, Ripples = false };
            WaterColumnImage sand = scene.Capture(settings);
            WaterColumnImage black = scene.Capture(settings with { BlackBed = true });
            WaterColumnImage bed = scene.Capture(settings with { BedOnly = true, Enabled = false, DryReference = true });
            Vector3 shadow = new(0f, 0f, 0.75f + depth * 0.778952f);
            Vector3 lit = shadow + new Vector3(1.5f, 0f, 0f);
            float dryContrast = L(bed.Sample(lit)) - L(bed.Sample(shadow));
            Assert.True(dryContrast > 0.02f, $"The dry bed shadow control must exist at {depth} m, got {dryContrast}.");
            float contrast = (L(sand.Sample(lit) - black.Sample(lit))
                - L(sand.Sample(shadow) - black.Sample(shadow))) / dryContrast;
            contrasts.Add(contrast);
            output.WriteLine($"depth={depth:F2}, normalized bed contrast={contrast:F6}, dry contrast={dryContrast:F6}");
        }
        Assert.True(contrasts[0] > contrasts[1] && contrasts[1] > contrasts[2] && contrasts[2] > contrasts[3]);
        Assert.True(contrasts[3] < 0.05f);
    }

    // Double tinting or using the legacy body colour would obscure a five-centimetre bed.
    [GpuFact]
    public void Golden_FiveCentimetresPreservesTheBed()
    {
        var settings = new WaterColumnCase { Depth = 0.05f, Caster = false, Ripples = false, TopDown = true, Orthographic = true };
        WaterColumnImage wet = scene.Capture(settings);
        WaterColumnImage bed = scene.Capture(settings with { BedOnly = true });
        AssertM2Lambert(bed, LitColumn);
        float error = MaxChannelAbs(wet.Sample(LitColumn) - bed.Sample(LitColumn));
        output.WriteLine($"5 cm bed RGB={bed.Sample(LitColumn)}, wet={wet.Sample(LitColumn)}, error={error:F6}");
        Assert.InRange(error, 0f, 0.035f);
    }

    // The CPU bed is already M2-lit. Attenuating its direct term a second time fails this absolute RGB bound.
    [GpuFact]
    public void Golden_ThirtyCentimetresMatchesCpuTransport()
    {
        var settings = new WaterColumnCase { Caster = false, Ripples = false };
        WaterColumnImage wet = scene.Capture(settings);
        WaterColumnImage bed = scene.Capture(settings with { BedOnly = true });
        AssertM2Lambert(bed, LitColumn);
        Vector3 expected = CpuExpected(wet, bed, LitColumn, settings.Depth);
        float error = MaxChannelAbs(wet.Sample(LitColumn) - expected);
        output.WriteLine($"30 cm RGB={wet.Sample(LitColumn)}, CPU={expected}, error={error:F6}");
        Assert.InRange(error, 0f, 0.02f);
    }

    // Incorrect reference units or normalization cannot reproduce the authored reachable deep colour.
    [GpuFact]
    public void Golden_DeepReferenceReproducesWaterColor()
    {
        var settings = new WaterColumnCase { ReferenceLight = true, Depth = 16f, Clarity = 2f,
            Caster = false, Ripples = false, TopDown = true, Orthographic = true };
        Vector3 reference = scene.Capture(settings).Sample(LitColumn);
        output.WriteLine($"reference deep RGB={reference}, target=(0.10,0.13,0.10)");
        Assert.InRange(MaxChannelAbs(reference - new Vector3(0.10f, 0.13f, 0.10f)), 0f, 0.02f);
    }

    // Surface extras must use the surface shadow, rather than the average column or an unshadowed key.
    [GpuFact]
    public void Golden_GlintAndSunFoamAreShadowed()
    {
        var glint = new WaterColumnCase { Eye = new Vector3(0f, 3f, 4.284444f), Ripples = false, GlintStrength = 1f };
        WaterColumnImage on = scene.Capture(glint), off = scene.Capture(glint with { GlintStrength = 0f });
        WaterColumnImage openOn = scene.Capture(glint with { Caster = false });
        WaterColumnImage openOff = scene.Capture(glint with { Caster = false, GlintStrength = 0f });
        float shadowGlintDelta = L(on.Sample(ShadowColumn) - off.Sample(ShadowColumn));
        // The same ray without the caster is the lit control, keeping the narrow specular lobe aligned.
        float litGlintDelta = L(openOn.Sample(ShadowColumn) - openOff.Sample(ShadowColumn));
        var foam = new WaterColumnCase { FoamStrength = 1f, BlackBed = true, WaterColor = Color.Black, Ripples = false };
        WaterColumnImage sun = scene.Capture(foam), ambient = scene.Capture(foam with { AmbientOnly = true });
        float shadowSunFoamDelta = L(sun.Sample(ShadowColumn) - ambient.Sample(ShadowColumn));
        float litSunFoamDelta = L(sun.Sample(LitColumn) - ambient.Sample(LitColumn));
        output.WriteLine($"glint shadow={shadowGlintDelta:F6}, lit={litGlintDelta:F6}, sun foam shadow={shadowSunFoamDelta:F6}, lit={litSunFoamDelta:F6}");
        Assert.InRange(shadowGlintDelta, 0f, 2f / 255f);
        Assert.True(litGlintDelta >= 0.03f);
        Assert.InRange(shadowSunFoamDelta, 0f, 2f / 255f);
        Assert.True(litSunFoamDelta >= 0.02f);
    }

    // Disabled authoring must not allocate or execute the colour copy, or perturb legacy output bytes.
    [GpuFact]
    public void Golden_NoOptInIsByteIdenticalAndSkipsCopy()
    {
        var settings = new WaterColumnCase { Enabled = false, NullColumnFields = true };
        WaterColumnImage allNullFields = scene.Capture(settings);
        WaterColumnImage explicitFalseWithNewValues = scene.Capture(settings with
        { NullColumnFields = false, WaterColor = new Color(0.7f, 0.2f, 0.4f), Clarity = 0.1f });
        Assert.Equal(allNullFields.Rgba, explicitFalseWithNewValues.Rgba);
        Assert.Equal(new WaterColumnDiagnostics(0, false, WaterColumnCopyPath.None), explicitFalseWithNewValues.Diagnostics);
        Assert.Equal(allNullFields.Diagnostics, explicitFalseWithNewValues.Diagnostics);
    }

    // An unresolved, stale, or multiplied MSAA source fails the same absolute transport on each legal AA path.
    [GpuFact]
    public void Golden_LegalAntiAliasingPathsKeepColumnTransport()
    {
        foreach (AntiAliasing aa in new[] { AntiAliasing.Off, AntiAliasing.Msaa(4), AntiAliasing.Temporal })
        {
            var settings = new WaterColumnCase { Aa = aa, Caster = false, Ripples = false };
            WaterColumnImage wet = scene.Capture(settings, 8);
            WaterColumnImage bed = scene.Capture(settings with { BedOnly = true }, 8);
            Vector3 expected = CpuExpected(wet, bed, LitColumn, 0.3f);
            Vector3 resolved = wet.ResolvedSample(LitColumn);
            output.WriteLine($"AA={aa}, resolved RGB={resolved}, CPU={expected}, path={wet.Diagnostics.CopyPath}");
            Assert.InRange(MaxChannelAbs(resolved - expected), 0f, 0.02f);
            Assert.Equal(new WaterColumnDiagnostics(1, true, aa.Mode == AntiAliasingMode.Msaa
                ? WaterColumnCopyPath.MsaaResolve : WaterColumnCopyPath.SingleSampleCopy), wet.Diagnostics);
        }
    }

    // Absolute-coordinate reconstruction or a perspective-only view ray breaks translated or orthographic water.
    [GpuFact]
    public void Golden_OriginAndOrthographicParity()
    {
        foreach (bool orthographic in new[] { false, true })
        {
            var settings = new WaterColumnCase { Orthographic = orthographic, Ripples = false, Bound = true };
            WaterColumnImage origin = scene.Capture(settings);
            WaterColumnImage translated = scene.Capture(settings with { Origin = new Vector3(4194304f, 128f, -4194304f) });
            float error = MaximumError(origin, translated);
            output.WriteLine($"orthographic={orthographic}, origin max RGB error={error:F6}");
            Assert.InRange(error, 0f, 0.02f);
            Assert.True(L(origin.Sample(LitColumn)) - L(origin.Sample(ShadowColumn)) >= 0.025f);
        }
    }

    // Aging bindings across every allocation discriminator must produce the same non-temporal image as a fresh scene.
    [GpuFact]
    public void Golden_SharedSceneMatchesFreshScene()
    {
        scene.Capture(new WaterColumnCase { Bound = true, Aa = AntiAliasing.Msaa(4), Size = 256 });
        scene.Capture(new WaterColumnCase { Enabled = false, Hdr = false, ShadowResolution = 1024, ShadowCascades = 3 });
        scene.Capture(new WaterColumnCase { Bound = true, AuthoredDepth = 1f, Aa = AntiAliasing.Temporal, Size = 384 });
        foreach (bool bound in new[] { false, true })
        {
            var settings = new WaterColumnCase { Bound = bound, MultiplePlanes = true };
            WaterColumnImage shared = scene.Capture(settings), fresh = scene.CaptureFresh(settings);
            float error = MaximumError(shared, fresh);
            output.WriteLine($"shared/fresh bound={bound}, max RGB error={error:F6}");
            Assert.InRange(error, 0f, 1f / 255f);
            Assert.Equal(new WaterColumnDiagnostics(2, true, WaterColumnCopyPath.SingleSampleCopy), shared.Diagnostics);
            Assert.True(MaxChannelAbs(shared.Sample(new Vector3(-2f, 0f, 1f))
                - shared.Sample(new Vector3(0f, 0f, 1f))) > 0.02f);
            WaterColumnImage otherLook = scene.Capture(settings with { WaterColor = new Color(0.7f, 0.1f, 0.1f) });
            Assert.InRange(MaxChannelAbs(shared.Sample(new Vector3(2f, 0f, 1f))
                - otherLook.Sample(new Vector3(2f, 0f, 1f))), 0f, 1f / 255f);
            Assert.InRange(MaxChannelAbs(shared.Sample(new Vector3(0f, 0f, 1f))
                - otherLook.Sample(new Vector3(0f, 0f, 1f))), 0f, 1f / 255f);
            Assert.True(MaxChannelAbs(shared.Sample(new Vector3(-2f, 0f, 1f))
                - otherLook.Sample(new Vector3(-2f, 0f, 1f))) > 0.02f);
        }
    }

    // Fog, zero coverage, no depth and submerged occluders must not introduce non-finite or spurious column light.
    [GpuFact]
    public void Golden_FogWaterlineAndMissingBedStayFinite()
    {
        foreach (bool atmosphere in new[] { false, true })
        {
            foreach (WaterColumnCase settings in new[]
            {
                new WaterColumnCase { Atmosphere = atmosphere, Depth = 0f, Caster = false },
                new WaterColumnCase { Atmosphere = atmosphere, BedOnly = true },
                new WaterColumnCase { Atmosphere = atmosphere, MissingBed = true, Caster = false },
                new WaterColumnCase { Atmosphere = atmosphere, Eye = new Vector3(0f, 0.001f, 5f), Caster = false },
                new WaterColumnCase { Atmosphere = atmosphere, SunElevationDegrees = -5f, AmbientOnly = true }
            })
            {
                WaterColumnImage image = scene.Capture(settings);
                Assert.All(image.LinearRgb, value => Assert.True(float.IsFinite(value)));
                if (settings.BedOnly)
                {
                    Assert.Equal(image.CopiedRgb, image.LinearRgb);
                    Assert.True(image.Diagnostics.CopyRan);
                }
            }
        }
        var noShadow = new WaterColumnCase { ShadowStrength = 0f, BlackBed = true, Bound = true, Ripples = false };
        WaterColumnImage casting = scene.Capture(noShadow), open = scene.Capture(noShadow with { Caster = false });
        Assert.InRange(MaxChannelAbs(casting.Sample(ShadowColumn) - open.Sample(ShadowColumn)), 0f, 2f / 255f);
        WaterColumnImage wet = scene.Capture(noShadow with { Caster = false, BlackBed = false });
        WaterColumnImage bed = scene.Capture(noShadow with { Caster = false, BlackBed = false, BedOnly = true });
        Vector3 zeroShadowExpected = CpuExpected(wet, bed, LitColumn, noShadow.Depth);
        float zeroShadowError = MaxChannelAbs(wet.Sample(LitColumn) - zeroShadowExpected);
        output.WriteLine($"zero shadow strength RGB={wet.Sample(LitColumn)}, CPU={zeroShadowExpected}, transport error={zeroShadowError:F6}, wet/bed difference={MaxChannelAbs(wet.Sample(LitColumn) - bed.Sample(LitColumn)):F6}");
        Assert.InRange(zeroShadowError, 0f, 0.02f);

        var rock = new WaterColumnCase { CasterTop = -0.05f, CasterBottom = -0.25f, Ripples = false };
        WaterColumnImage rockBed = scene.Capture(rock with { BedOnly = true });
        WaterColumnImage openBed = scene.Capture(rock with { BedOnly = true, Caster = false });
        Vector3 bedProbe = new(0f, -0.3f, 0.62f);
        Assert.True(L(openBed.Sample(bedProbe)) - L(rockBed.Sample(bedProbe)) > 0.025f);
        WaterColumnImage rockColumn = scene.Capture(rock with { BlackBed = true, Bound = true });
        WaterColumnImage openColumn = scene.Capture(rock with { BlackBed = true, Caster = false, Bound = true });
        Assert.InRange(MaxChannelAbs(rockColumn.Sample(ShadowColumn) - openColumn.Sample(ShadowColumn)), 0f, 2f / 255f);

        var capped = new WaterColumnCase { Bound = true, Depth = 2f, AuthoredDepth = 0.3f, Caster = false, Ripples = false };
        WaterColumnImage cap = scene.Capture(capped), capBed = scene.Capture(capped with { BedOnly = true });
        Assert.InRange(MaxChannelAbs(cap.Sample(LitColumn) - CpuExpected(cap, capBed, LitColumn, 0.3f)), 0f, 0.02f);
        WaterColumnImage deep = scene.Capture(capped with { Bound = false });
        Assert.True(MaxChannelAbs(cap.Sample(LitColumn) - deep.Sample(LitColumn)) > 0.02f);
        WaterColumnImage fft = scene.Capture(new WaterColumnCase { Bound = false, WaveSource = WaterWaveSource.FftOcean });
        Assert.All(fft.LinearRgb, value => Assert.True(float.IsFinite(value)));
        Assert.True(fft.Diagnostics.CopyRan);
        Assert.True(L(fft.Sample(LitColumn)) - L(fft.Sample(ShadowColumn)) >= 0.025f);
        var fftControl = new WaterColumnCase { Caster = false, Ripples = false, WaveSource = WaterWaveSource.FftOcean };
        WaterColumnImage fftWet = scene.Capture(fftControl), fftBed = scene.Capture(fftControl with { BedOnly = true });
        Assert.InRange(MaxChannelAbs(fftWet.Sample(LitColumn) - CpuExpected(fftWet, fftBed, LitColumn, 0.3f)), 0f, 0.02f);
    }

    // Re-emit current isolated programs. A stale default HLSL file must not serve as binding evidence.
    [GpuFact]
    public void Golden_FragmentVariantsEmitCurrentIsolatedHlsl()
    {
        Directory.CreateDirectory(WaterColumnRiverScene.Scratch);
        string[] names = ["Water", "WaterClipmap", "WaterTriangle", "WaterClipmapTriangle"];
        ShippedGraphicsProgram[] programs = ShippedShaderPrograms.GraphicsPrograms().Where(p => names.Contains(p.Name)).ToArray();
        Assert.Equal(4, programs.Length);
        foreach (ShippedGraphicsProgram program in programs)
        {
            byte[] vertex = SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name);
            byte[] fragment = SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name);
            CrossCompiledPair hlsl = SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, program.Name);
            Assert.Equal(17, Assert.Single(hlsl.Reflection.ResourceLayouts).Elements.Length);
            File.WriteAllText(Path.Combine(WaterColumnRiverScene.Scratch, program.Name + ".frag.hlsl"), hlsl.FragmentSource);
            output.WriteLine($"{program.Name}.frag.hlsl SHA256={Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hlsl.FragmentSource)))}");
        }
    }

    static void AssertM2Lambert(WaterColumnImage bed, Vector3 probe)
    {
        WaterColumnCase s = bed.Settings;
        WaterColumnCoefficients c = WaterColumnOptics.Derive(s.WaterColor, s.Clarity);
        float sine = MathF.Sin(s.SunElevationDegrees * MathF.PI / 180f);
        float mu0 = MathF.Sqrt(1f - (1f - sine * sine) / (1.333f * 1.333f));
        Vector3 attenuation = new(MathF.Exp(-c.Extinction.X * s.Depth / mu0),
            MathF.Exp(-c.Extinction.Y * s.Depth / mu0), MathF.Exp(-c.Extinction.Z * s.Depth / mu0));
        Vector3 expected = new Vector3(0.65f, 0.58f, 0.42f) * (new Vector3(0.1f) + attenuation * sine);
        Assert.InRange(MaxChannelAbs(bed.Sample(probe) - expected), 0f, 0.005f);
    }

    internal static Vector3 CpuExpected(WaterColumnImage wet, WaterColumnImage bed, Vector3 probe, float depth)
    {
        WaterColumnCase s = wet.Settings;
        WaterColumnCoefficients c = WaterColumnOptics.Derive(s.WaterColor, s.Clarity);
        float sine = MathF.Sin(s.SunElevationDegrees * MathF.PI / 180f);
        float mu0 = MathF.Sqrt(1f - (1f - sine * sine) / (1.333f * 1.333f));
        Vector2 p = wet.Project(probe);
        Vector3 sum = Vector3.Zero;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = (int)p.X + dx, y = (int)p.Y + dy;
                Vector3 incident = wet.Incident(x, y);
                float mu = MathF.Sqrt(1f - (1f - incident.Y * incident.Y) / (1.333f * 1.333f));
                Vector3 transported = WaterColumnComposite.Transmitted(c, bed.Pixel(x, y), Vector3.One,
                    new Vector3(0.1f), mu, mu0, depth / mu, [1f, 1f, 1f, 1f], 0.5f);
                float fresnel = MathF.Pow(1f - MathF.Max(-incident.Y, 0f), 5f);
                sum += transported * (1f - fresnel);
            }
        return sum / 9f;
    }

    internal static float MaximumError(WaterColumnImage a, WaterColumnImage b)
    {
        Assert.Equal(a.LinearRgb.Length, b.LinearRgb.Length);
        float maximum = 0f;
        for (int i = 0; i < a.LinearRgb.Length; i++) maximum = MathF.Max(maximum, MathF.Abs(a.LinearRgb[i] - b.LinearRgb[i]));
        return maximum;
    }

    static float AttachedBoundaryDistance(WaterColumnImage shadow, WaterColumnImage open)
    {
        int size = shadow.Size;
        var dark = new bool[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector3 s = shadow.SurfacePoint(x, y);
                // Exclude the box footprint and its above-water silhouette from the component.
                Vector3 incident = shadow.Incident(x, y);
                dark[y * size + x] = !HitsAboveWaterBox(s, -incident)
                    && L(open.Pixel(x, y) - shadow.Pixel(x, y)) >= 0.025f;
            }
        Vector2 probe = shadow.Project(ShadowColumn);
        int start = (int)probe.Y * size + (int)probe.X;
        Assert.True(dark[start], "The shadow-column probe must belong to the dark water component, outside the silhouette.");
        var visited = new bool[dark.Length];
        var queue = new Queue<int>();
        queue.Enqueue(start);
        visited[start] = true;
        Vector2 a = shadow.Project(new Vector3(-0.5f, 0f, 0.5f));
        Vector2 b = shadow.Project(new Vector3(0.5f, 0f, 0.5f));
        Vector2 c = shadow.Project(new Vector3(0.5f, 0f, -0.5f));
        float nearest = float.PositiveInfinity;
        int count = 0;
        while (queue.TryDequeue(out int i))
        {
            int x = i % size, y = i / size;
            bool boundary = false;
            foreach ((int dx, int dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) { boundary = true; continue; }
                int next = ny * size + nx;
                if (!dark[next]) boundary = true;
                else if (!visited[next]) { visited[next] = true; queue.Enqueue(next); }
            }
            if (boundary)
            {
                Vector2 pixel = new(x + 0.5f, y + 0.5f);
                nearest = MathF.Min(nearest, MathF.Min(SegmentDistance(pixel, a, b), SegmentDistance(pixel, b, c)));
            }
            count++;
        }
        Assert.True(count >= 9, "A one-pixel witness cannot establish an attached column shadow.");
        return nearest;
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    static bool HitsAboveWaterBox(Vector3 surface, Vector3 towardEye)
    {
        float enter = 0f, leave = 0.6f / towardEye.Y;
        return Clip(surface.X, towardEye.X) && Clip(surface.Z, towardEye.Z);

        bool Clip(float position, float direction)
        {
            if (MathF.Abs(direction) < 1e-7f) return MathF.Abs(position) <= 0.5f;
            float a = (-0.5f - position) / direction, b = (0.5f - position) / direction;
            enter = MathF.Max(enter, MathF.Min(a, b));
            leave = MathF.Min(leave, MathF.Max(a, b));
            return enter <= leave;
        }
    }
}
