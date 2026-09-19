using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Point-light shadows END TO END on a real device: a light queued with a <see cref="LightShadow"/>, an atlas
/// allocated and filled inside an ordinary frame, and a receiver that is visibly darker where a wall stands
/// between it and the light.
/// <para>
/// EVERY ASSERTION IS RELATIVE and there is no golden image: a probe is compared against another probe in the
/// same picture, or against the same picture rendered with <see cref="LightShadow.None"/>. So the class runs on
/// every backend the matrix has with nothing to bake, and a change in tone mapping or in the light falloff moves
/// both sides of every comparison together.
/// </para>
/// </summary>
public sealed class PointShadowGpuTests(PointShadowScene fixture) : IClassFixture<PointShadowScene>
{
    /// <summary>Where the light stands for the wall and gap cases, and how far it reaches.</summary>
    static readonly Vector3 Light = new(0f, 2f, 0f);
    const float Radius = 12f;

    /// <summary>The floor probe the wall shadows, and its mirror on the open side of the light. The two are the
    /// same distance from the light and the same angle to it, so with no wall they read the same level and every
    /// difference between them is the shadow.</summary>
    static readonly Vector3 Shadowed = new(5f, 0f, 0f);
    static readonly Vector3 Open = new(-5f, 0f, 0f);

    [GpuFact]
    public void TheFloorIsActuallyLitByThePointLight()
    {
        // The control every relative assertion below leans on. A floor that was black in every capture would make
        // "darker than" trivially true, so pin that the light reaches it first, and that the two probes agree
        // with no wall in the way.
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.DrawFloor();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
        });

        Assert.True(shot.Red(Open) > 60, $"the open floor probe reads {shot.Red(Open)}, which is not lit");
        Assert.True(Math.Abs(shot.Red(Shadowed) - shot.Red(Open)) <= 4,
            $"the two probes must match with no wall: {shot.Red(Shadowed)} against {shot.Red(Open)}");
    }

    [GpuFact]
    public void AWallBetweenTheLightAndTheFloorDarkensIt()
    {
        PointShadowScene.Shot shadowed = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(101));
        });
        PointShadowScene.Shot unshadowed = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
        });

        Assert.True(shadowed.Red(Shadowed) <= shadowed.Red(Open) - 20,
            $"the floor behind the wall reads {shadowed.Red(Shadowed)} against the open side's "
            + $"{shadowed.Red(Open)}, so the wall is casting nothing");
        Assert.True(Math.Abs(unshadowed.Red(Shadowed) - unshadowed.Red(Open)) <= 4,
            $"with LightShadow.None the same two probes must match: {unshadowed.Red(Shadowed)} against "
            + $"{unshadowed.Red(Open)}");
        Assert.Equal(1, shadowed.ShadowedLights);
        Assert.Equal(0, unshadowed.ShadowedLights);
    }

    [GpuFact]
    public void ADoorwayGapLetsTheLightStraightThrough()
    {
        // The same wall with a gap on the z axis. The probe straight out from the light now sees it through the
        // opening, and the one two metres to the side is still behind the wall.
        Vector3 offset = new(5f, 0f, 2f);
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawSplitWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(102));
        });

        Assert.True(Math.Abs(shot.Red(Shadowed) - shot.Red(Open)) <= 8,
            $"the gap must let the light through: {shot.Red(Shadowed)} against the open side's {shot.Red(Open)}");
        Assert.True(shot.Red(offset) <= shot.Red(Open) - 20,
            $"the floor beside the gap reads {shot.Red(offset)} against {shot.Red(Open)}, so the halves of the "
            + "split wall are casting nothing");
    }

    [GpuFact]
    public void AStaticMapIsNotRebuiltWhileNothingUnderItMoves()
    {
        // Three frames after the warm-up. The warm-up only brought the atlas up (it allocates nothing itself and
        // rendered nothing into it), so the row is still drawn exactly once and then reused for as long as the
        // scene stands still.
        IReadOnlyList<PointShadowScene.Shot> shots = fixture.Many(3, (s, _) =>
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(103));
        });

        Assert.Equal(1, shots[0].Diagnostics.PointStaticRebuilds);
        Assert.Equal(0, shots[1].Diagnostics.PointStaticRebuilds);
        Assert.Equal(0, shots[2].Diagnostics.PointStaticRebuilds);
        Assert.Equal(0, shots[1].Diagnostics.PointFaceDrawCalls);
        Assert.Equal(0, shots[2].Diagnostics.PointFaceDrawCalls);
        // The cached row is still sampled on the later frames, so the picture is the same bytes rather than an
        // unshadowed one.
        Assert.Equal(shots[0].Rgba, shots[1].Rgba);
        Assert.Equal(shots[0].Rgba, shots[2].Rgba);
        Assert.Equal(1, shots[2].ShadowedLights);
    }

    [GpuFact]
    public void AStaticMapIsRebuiltWhenItsCastersMove()
    {
        IReadOnlyList<PointShadowScene.Shot> shots = fixture.Many(2, (s, frame) =>
        {
            s.DrawFloor();
            // The wall slides one metre down the z axis on the second frame, which moves the shadow it throws
            // without moving the light or changing anything else.
            s.DrawWall(zOffset: frame == 0 ? 0f : 1f);
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(104));
        });

        Assert.Equal(1, shots[0].Diagnostics.PointStaticRebuilds);
        Assert.Equal(1, shots[1].Diagnostics.PointStaticRebuilds);
        Assert.NotEqual(shots[0].Rgba, shots[1].Rgba);
    }

    [GpuFact]
    public void ADynamicMapFollowsItsLightEveryFrame()
    {
        // The light crosses to the far side of the wall, so what it shadows changes completely.
        IReadOnlyList<PointShadowScene.Shot> shots = fixture.Many(2, (s, frame) =>
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(frame == 0 ? Light : Light + new Vector3(6f, 0f, 0f),
                Color.White, Radius, PointShadowScene.Intensity, LightShadow.Dynamic);
        });

        Assert.Equal(1, shots[0].Diagnostics.PointDynamicRenders);
        Assert.Equal(1, shots[1].Diagnostics.PointDynamicRenders);
        Assert.Equal(0, shots[0].Diagnostics.PointStaticRebuilds);
        // Frame 0 shadows the far probe. Frame 1 has the light on that side of the wall, so it does not.
        Assert.True(shots[0].Red(Shadowed) <= shots[0].Red(Open) - 20,
            $"frame 0 must shadow the far probe: {shots[0].Red(Shadowed)} against {shots[0].Red(Open)}");
        Assert.True(shots[1].Red(Shadowed) > shots[0].Red(Shadowed) + 20,
            $"the light moved past the wall, so the far probe must brighten: {shots[1].Red(Shadowed)} against "
            + $"{shots[0].Red(Shadowed)}");
    }

    [GpuFact]
    public void AFrameThatAsksForNothingIsTheSameBytesWhicheverWayTheSettingIsSet()
    {
        byte[] on = fixture.One(s =>
        {
            s.Settings.Enabled = true;
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
            s.AddLight(Open + Vector3.UnitY * 2f, Color.White, Radius, PointShadowScene.Intensity);
        }).Rgba;
        byte[] off = fixture.One(s =>
        {
            s.Settings.Enabled = false;
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
            s.AddLight(Open + Vector3.UnitY * 2f, Color.White, Radius, PointShadowScene.Intensity);
        }).Rgba;

        Assert.Equal(on, off);
    }

    [GpuFact]
    public void StaticLightsKeepTheirMapsBeyondTheConfiguredEffectBudget()
    {
        // The configured floor is one, but both static owners retain maps. The far light cannot reach the
        // measured floor, so the near light's own map must still block its contribution behind the wall.
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.Settings.MaxShadowedLights = 1;
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light + new Vector3(0f, 0f, -20f), Color.White, 8f, PointShadowScene.Intensity,
                LightShadow.Static(105));
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(106));
        });

        Assert.Equal(2, shot.ShadowedLights);
        Assert.Equal(2, shot.Diagnostics.PointShadowedLights);
        Assert.Equal(2, shot.Diagnostics.PointSlotsInUse);
        Assert.True(shot.Red(Shadowed) <= shot.Red(Open) - 20,
            $"the near light must retain its own wall shadow when both maps are resident: "
            + $"{shot.Red(Shadowed)} against {shot.Red(Open)}");
    }

    /// <summary>
    /// THE SLOT TABLE IS INDEXED BY THE UPLOADED LIGHT ORDER, not by the order the budget ranked the requests in.
    /// Two lights are queued in one order and rank in the other, and only the SECOND one asks for a map. Each
    /// carries its own colour, so the two channels say separately whether the right light was shadowed: the red
    /// light's contribution must drop behind the wall while the blue light's does not.
    /// <para>
    /// A table written at the request index instead would leave the red light unshadowed (its own entry reading
    /// -1) and hand the blue light a row built for a light standing somewhere else, which is the exact failure
    /// this is here to catch.
    /// </para>
    /// </summary>
    [GpuFact]
    public void TheSlotTableIsAlignedToTheUploadedLightOrderAndNotToTheBudgetOrder()
    {
        // Queued first, ranks SECOND (it is further from the eye) and asks for nothing. Standing well off the z
        // axis, it reaches both probes equally, so it can never be the reason the two differ.
        Vector3 blue = new(0f, 2f, -8f);
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(blue, new Color(0f, 0f, 1f, 1f), 20f, PointShadowScene.Intensity, LightShadow.None);
            s.AddLight(Light, new Color(1f, 0f, 0f, 1f), Radius, PointShadowScene.Intensity,
                LightShadow.Static(107));
        });

        Assert.True(shot.Red(Shadowed) <= shot.Red(Open) - 20,
            $"the RED light is the one that asked, so its contribution must drop behind the wall: "
            + $"{shot.Red(Shadowed)} against {shot.Red(Open)}");
        Assert.True(Math.Abs(shot.Blue(Shadowed) - shot.Blue(Open)) <= 8,
            $"the BLUE light asked for nothing, so it must light both probes the same: {shot.Blue(Shadowed)} "
            + $"against {shot.Blue(Open)}. A slot table written at the request index hands it somebody "
            + "else's atlas row.");
    }

    /// <summary>
    /// A caster the CAMERA cannot see still shadows, because the point pass draws the whole uploaded instance
    /// buffer and only the main pass is frustum culled. The wall here is projected off the side of the picture and
    /// the floor it darkens is in the middle of it.
    /// </summary>
    [GpuFact]
    public void ACasterOffTheSideOfThePictureStillShadows()
    {
        Vector3 farLight = new(0f, 2f, 18f);
        Vector3 under = new(0f, 0f, 0f);
        Vector3 beside = new(6f, 0f, 0f);

        PointShadowScene.Shot shadowed = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawOffscreenPillar();
            s.AddLight(farLight, Color.White, 26f, PointShadowScene.FarIntensity, LightShadow.Static(108));
        });
        PointShadowScene.Shot unshadowed = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawOffscreenPillar();
            s.AddLight(farLight, Color.White, 26f, PointShadowScene.FarIntensity, LightShadow.None);
        });

        Assert.False(fixture.OnScreen(PointShadowScene.OffscreenPillarCentre),
            "the pillar must be off the picture for this to be about an off-camera caster");
        Assert.True(shadowed.Red(under) <= unshadowed.Red(under) - 20,
            $"the floor in line with the off-camera pillar reads {shadowed.Red(under)} shadowed against "
            + $"{unshadowed.Red(under)} unshadowed, so an off-camera caster is being dropped");
        Assert.True(Math.Abs(shadowed.Red(beside) - unshadowed.Red(beside)) <= 6,
            $"the floor beside the pillar's line is not behind it, so it must not darken: "
            + $"{shadowed.Red(beside)} against {unshadowed.Red(beside)}");
    }

    /// <summary>
    /// TWO DYNAMIC LIGHTS IN ONE FRAME ARE TWO ROWS, each holding its own light's map. A dynamic light carries no
    /// key of its own, so the scene keys it by its place in the light queue, and nothing else pins that two of
    /// them are keyed apart: one shared key would give the second light the first one's row, and it would cast
    /// the first light's shadow from the second light's position.
    /// <para>
    /// Each light stands over its own wall five metres from the other, and each is a channel of its own, so the
    /// red probes measure the red light with the blue one contributing nothing to that number. A single shared
    /// row shows up as one of the two shadows landing in the wrong place.
    /// </para>
    /// </summary>
    [GpuFact]
    public void TwoDynamicLightsInOneFrameEachShadowFromTheirOwnPosition()
    {
        Vector3 redLight = new(0f, 2f, -4f), blueLight = new(0f, 2f, 4f);
        Vector3 redDark = new(5f, 0f, -4f), redLit = new(-5f, 0f, -4f);
        Vector3 blueDark = new(5f, 0f, 4f), blueLit = new(-5f, 0f, 4f);

        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawWall(zOffset: -4f);
            s.DrawWall(zOffset: 4f);
            s.AddLight(redLight, new Color(1f, 0f, 0f, 1f), Radius, PointShadowScene.Intensity,
                LightShadow.Dynamic);
            s.AddLight(blueLight, new Color(0f, 0f, 1f, 1f), Radius, PointShadowScene.Intensity,
                LightShadow.Dynamic);
        });

        foreach (Vector3 probe in new[] { redDark, redLit, blueDark, blueLit })
            Assert.True(fixture.OnScreen(probe), $"the probe at {probe} is off the picture");
        Assert.Equal(2, shot.ShadowedLights);
        Assert.Equal(2, shot.Diagnostics.PointDynamicRenders);
        Assert.Equal(2, shot.Diagnostics.PointSlotsInUse);
        Assert.True(shot.Red(redDark) <= shot.Red(redLit) - 20,
            $"the first dynamic light must be shadowed by its own wall: {shot.Red(redDark)} against "
            + $"{shot.Red(redLit)}");
        Assert.True(shot.Blue(blueDark) <= shot.Blue(blueLit) - 20,
            $"the second dynamic light must be shadowed by ITS own wall, from its own position: "
            + $"{shot.Blue(blueDark)} against {shot.Blue(blueLit)}. Two dynamic lights sharing one atlas row "
            + "is what this reads like.");
    }

    /// <summary>
    /// A DYNAMIC LIGHT NEVER INHERITS ANOTHER LIGHT'S MAP. A dynamic light carries no key, so the scene keys it by
    /// its place in the light queue, and that place belongs to somebody else as soon as the queue changes. With a
    /// dynamic budget of one, the near light draws its row and the far one is not drawn at all. Queue them the
    /// other way round on the next frame and the far light now holds the near light's key: a row kept across the
    /// frame boundary would hand it a map baked around the OTHER light, and it would cast that light's wall as a
    /// shadow of its own, from a position four metres away, for as long as it stayed past the budget.
    /// <para>
    /// Each light owns a channel, so the blue numbers are the far light alone. Its two probes are symmetric about
    /// it with nothing between them and it, so they must read the same: the inherited map darkens one of them.
    /// </para>
    /// </summary>
    [GpuFact]
    public void ADynamicLightPastTheBudgetIsUnshadowedRatherThanShadowedFromAnotherLightsRow()
    {
        Vector3 near = new(0f, 2f, 4f), far = new(0f, 2f, -4f);
        Vector3 nearDark = new(5f, 0f, 4f), nearLit = new(-5f, 0f, 4f);
        Vector3 farDark = new(5f, 0f, -4f), farLit = new(-5f, 0f, -4f);
        Assert.True((near - fixture.Eye).Length() < (far - fixture.Eye).Length(),
            "this case needs the red light to rank first, so the budget draws it and not the blue one");

        IReadOnlyList<PointShadowScene.Shot> shots = fixture.Many(3, (s, frame) =>
        {
            s.DrawFloor();
            s.DrawWall(zOffset: 4f);                      // the NEAR light's wall, and the only one until frame 2
            if (frame == 2) s.DrawWall(zOffset: -4f);     // the far light's own, once it is the one being drawn
            // Frame 0 queues the near light first, frame 1 queues the far one first. The near light is nearer
            // either way, so the budget still draws it, and the far light inherits the key it just vacated.
            if (frame == 1) s.AddLight(far, new Color(0f, 0f, 1f, 1f), Radius, PointShadowScene.Intensity,
                LightShadow.Dynamic);
            if (frame != 2) s.AddLight(near, new Color(1f, 0f, 0f, 1f), Radius, PointShadowScene.Intensity,
                LightShadow.Dynamic);
            if (frame != 1) s.AddLight(far, new Color(0f, 0f, 1f, 1f), Radius, PointShadowScene.Intensity,
                LightShadow.Dynamic);
        }, new PointShadowSettings { MaxDynamicLightsPerFrame = 1 });

        // Frame 1: one light drawn, one light shadowed, and the far light lit evenly on both sides of itself.
        Assert.Equal(1, shots[1].Diagnostics.PointDynamicRenders);
        Assert.Equal(1, shots[1].ShadowedLights);
        Assert.True(shots[1].Red(nearDark) <= shots[1].Red(nearLit) - 20,
            $"the drawn light must still shadow: {shots[1].Red(nearDark)} against {shots[1].Red(nearLit)}");
        Assert.True(Math.Abs(shots[1].Blue(farDark) - shots[1].Blue(farLit)) <= 8,
            $"the light past the budget must be UNSHADOWED: {shots[1].Blue(farDark)} against "
            + $"{shots[1].Blue(farLit)}. A row kept from the frame before hands it the other light's map.");

        // Frame 2: the near light is gone, so the far one is inside the budget and shadows from its OWN position.
        Assert.Equal(1, shots[2].Diagnostics.PointDynamicRenders);
        Assert.Equal(1, shots[2].ShadowedLights);
        Assert.True(shots[2].Blue(farDark) <= shots[2].Blue(farLit) - 20,
            $"drawn at last, it must shadow from where it stands: {shots[2].Blue(farDark)} against "
            + $"{shots[2].Blue(farLit)}");
    }

    /// <summary>
    /// A BIGGER PROFILE RESHAPES THE ATLAS AND THE SHADOW SURVIVES IT. The reshape frees the texture every cached
    /// row lived in, so the row has to be drawn again into the new one before its light may sample it. A light
    /// still reading its old slot would sample freshly allocated memory, and a cache that forgot the light
    /// outright would leave the floor unshadowed: the assertion is that neither happened.
    /// </summary>
    [GpuFact]
    public void AHigherBudgetReshapesTheAtlasAndTheShadowIsStillThere()
    {
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(110));
        }, ShadowSettings.ForDetail(ShadowMapDetail.High).PointShadows);

        Assert.Equal(new PointShadowResolution(true, 384, 12, false, null), fixture.Resolved);
        Assert.Equal(1, shot.ShadowedLights);
        Assert.True(shot.Red(Shadowed) <= shot.Red(Open) - 20,
            $"the wall must still be casting after the reshape: {shot.Red(Shadowed)} against {shot.Red(Open)}");
    }

    /// <summary>
    /// A BIGGER FACE AT THE SAME ROW COUNT IS STILL A NEW TEXTURE. The row count is what the slot cache is sized
    /// to, so this reshape keeps every owner and only the pixels under them go away. A row left reading as drawn
    /// would have its light sample freshly allocated R32F, which reads as zero, which the compare takes as fully
    /// occluded: the light goes BLACK rather than stale. So the row has to come back undrawn, be re-rendered, and
    /// light the floor exactly as it did before.
    /// </summary>
    [GpuFact]
    public void AFaceResolutionChangeAtTheSameRowCountRedrawsTheRowRatherThanSamplingIt()
    {
        IReadOnlyList<PointShadowScene.Shot> shots = fixture.Many(3, (s, frame) =>
        {
            if (frame == 1) s.Settings.FaceResolution = 384;   // adopted at the next frame boundary
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(112));
        });

        Assert.Equal(new PointShadowResolution(true, 384, 8, false, null), fixture.Resolved);
        Assert.Equal(1, shots[2].Diagnostics.PointStaticRebuilds);
        Assert.Equal(1, shots[2].ShadowedLights);
        Assert.True(shots[2].Red(Open) > 60,
            $"the open floor reads {shots[2].Red(Open)} after the reshape, which is a light sampling a texture "
            + "nothing has drawn into rather than a shadow");
        Assert.True(shots[2].Red(Shadowed) <= shots[2].Red(Open) - 20,
            $"the wall must still cast after the reshape: {shots[2].Red(Shadowed)} against {shots[2].Red(Open)}");
    }

    /// <summary>
    /// THE LOW PROFILE GIVES THE MEMORY BACK AND THE PICTURE GOES BACK TO WHAT IT WAS. Turning point shadows off
    /// is not a darker or a softer picture, it is the SAME BYTES as a scene that never asked, because the
    /// receiver's slot table reads -1 and the sample, the texture read and the multiply are all skipped.
    /// </summary>
    [GpuFact]
    public void TheLowProfileReleasesTheAtlasAndRendersLikeNoRequestAtAll()
    {
        void Scene(PointShadowScene.Frame s, LightShadow shadow)
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, shadow);
        }

        PointShadowScene.Shot shadowed = fixture.One(s => Scene(s, LightShadow.Static(111)));
        Assert.Equal(1, shadowed.ShadowedLights);

        PointShadowScene.Shot off = fixture.One(s => Scene(s, LightShadow.Static(111)),
            ShadowSettings.ForDetail(ShadowMapDetail.Low).PointShadows);
        bool released = !fixture.HasAtlas;
        PointShadowResolution resolved = fixture.Resolved;
        byte[] never = fixture.One(s => Scene(s, LightShadow.None)).Rgba;

        Assert.True(released, "the disabled profile must give the atlas back rather than keep it resident");
        Assert.False(resolved.Enabled);
        Assert.Equal(0, off.ShadowedLights);
        Assert.Equal(never, off.Rgba);
        // The control: the two captures being equal would say nothing if the shadowed one matched them too.
        Assert.NotEqual(never, shadowed.Rgba);
    }

    /// <summary>
    /// THE ACNE PROOF. The pass stores the nearest surface with no face culling, so a flat floor stores its own
    /// distance and then compares against it, and the whole defence against self-shadowing is the two bias
    /// defaults. A floor with nothing on it must therefore render the same shadowed as unshadowed, all the way
    /// out to where the light is grazing it.
    /// <para>
    /// The tolerance is three levels and it is not negotiable: a failure here is tuned out through
    /// <see cref="PointShadowSettings.Bias"/> and <see cref="PointShadowSettings.SlopeBias"/>, which is where the
    /// trade against peter-panning belongs, never by widening this.
    /// </para>
    /// </summary>
    [GpuFact]
    public void AnUnoccludedFloorUnderAShadowedLightIsNotAcned()
    {
        PointShadowScene.Shot shadowed = fixture.One(s =>
        {
            s.DrawFloor();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(109));
        });
        PointShadowScene.Shot plain = fixture.One(s =>
        {
            s.DrawFloor();
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
        });

        Assert.Equal(1, shadowed.ShadowedLights);
        var worst = (Probe: Vector3.Zero, Delta: 0);
        for (float x = 0f; x <= Radius * 0.8f; x += 0.4f)
        {
            var probe = new Vector3(x, 0f, 0f);
            int delta = Math.Abs(shadowed.Red(probe) - plain.Red(probe));
            if (delta > worst.Delta) worst = (probe, delta);
        }

        Assert.True(worst.Delta <= 3,
            $"the floor at {worst.Probe} reads {shadowed.Red(worst.Probe)} with a shadow map against "
            + $"{plain.Red(worst.Probe)} without one, a gap of {worst.Delta} levels. That is the floor "
            + "shadowing itself. Raise the PointShadowSettings bias defaults, never this tolerance.");
    }
}
