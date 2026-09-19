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
    public void PastTheBudgetOnlyTheNearerLightKeepsItsMap()
    {
        // Two static requests against a one-row atlas. The far light loses, so the wall shadows the near light's
        // contribution and nothing else.
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.Settings.MaxShadowedLights = 1;
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(Light + new Vector3(0f, 0f, -20f), Color.White, 8f, PointShadowScene.Intensity,
                LightShadow.Static(105));
            s.AddLight(Light, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(106));
        });

        Assert.Equal(1, shot.ShadowedLights);
        Assert.Equal(1, shot.Diagnostics.PointShadowedLights);
        Assert.Equal(1, shot.Diagnostics.PointSlotsInUse);
        Assert.True(shot.Red(Shadowed) <= shot.Red(Open) - 20,
            $"the nearer light is the one that kept its map, so its shadow must be on the floor: "
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

/// <summary>
/// The one scene <see cref="PointShadowGpuTests"/> renders every case through, held for the whole class as a
/// class fixture (the <c>OceanFocusScene</c> rule). A flat floor, a couple of box casters and a camera looking
/// almost straight down, so a world point on the floor maps to a picture pixel through
/// <see cref="IsoCamera3D.WorldToScreen"/> and every probe is named in world units.
/// </summary>
/// <remarks>
/// The device is built LAZILY on the first capture, so a plain <c>dotnet test</c> that skips every
/// <c>[GpuFact]</c> never asks for one. The globals are darkened to nothing so the picture is the point light's
/// own contribution: a frame lit by the key light would be equally bright shadowed and unshadowed, and every
/// relative assertion in the class would pass for no reason.
/// </remarks>
public sealed class PointShadowScene : IDisposable
{
    /// <summary>Capture size. Small on purpose: these run on lavapipe and WARP as well as Metal, and every claim
    /// is made about a handful of probes rather than about the picture.</summary>
    public const int Width = 192, Height = 192;

    /// <summary>The point light intensity the near cases use, chosen so the lit floor sits well clear of both
    /// black and saturation: a probe pinned at 255 could not show a shadow softening and one near 0 could not
    /// show acne.</summary>
    public const float Intensity = 1.6f;

    /// <summary>The intensity the off-camera case uses, which stands its light fourteen metres away.</summary>
    public const float FarIntensity = 20f;

    /// <summary>Where the off-camera pillar stands, well past the framed floor.</summary>
    public static Vector3 OffscreenPillarCentre => new(0f, 1.5f, 16f);

    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    MeshHandle _floor;
    MeshHandle _box;

    /// <summary>One captured frame: its pixels, the shadow diagnostics it published, and how many point lights it
    /// reported as carrying a map. Probes are named in WORLD units and resolved through the camera.</summary>
    public sealed class Shot(byte[] rgba, ShadowPassDiagnostics diagnostics, int shadowedLights, IIsoCamera3D camera)
    {
        public byte[] Rgba { get; } = rgba;
        public ShadowPassDiagnostics Diagnostics { get; } = diagnostics;
        public int ShadowedLights { get; } = shadowedLights;

        /// <summary>The red channel averaged over a 3 by 3 patch at <paramref name="world"/>, which is the
        /// brightness every assertion in the class is made in.</summary>
        public int Red(Vector3 world) => Channel(world, 0);

        /// <summary>The blue channel at <paramref name="world"/>, which the slot-order case reads separately from
        /// the red one.</summary>
        public int Blue(Vector3 world) => Channel(world, 2);

        int Channel(Vector3 world, int offset)
        {
            camera.WorldToScreen(world, Width, Height, out Vector2 px);
            int cx = Math.Clamp((int)px.X, 1, Width - 2);
            int cy = Math.Clamp((int)px.Y, 1, Height - 2);
            int sum = 0;
            for (int y = cy - 1; y <= cy + 1; y++)
                for (int x = cx - 1; x <= cx + 1; x++)
                    sum += Rgba[(y * Width + x) * 4 + offset];
            return sum / 9;
        }
    }

    /// <summary>What a case hands its frame: the queue calls it needs, plus the settings object it may tune. A
    /// thin façade over <see cref="Scene3D"/> so a case reads as a scene description rather than as matrix
    /// arithmetic.</summary>
    public sealed class Frame(Scene3D scene, MeshHandle floor, MeshHandle box)
    {
        /// <summary>This capture's point-shadow settings, reset to the shipped defaults before every capture, so
        /// a case that tunes one knob cannot reach the next case.</summary>
        public PointShadowSettings Settings => scene.Post.Quality.Shadows.PointShadows;

        /// <summary>The flat receiving floor, forty metres square at y = 0.</summary>
        public void DrawFloor() => scene.Draw(floor, Matrix4x4.Identity, new Color(1f, 1f, 1f, 1f));

        /// <summary>The wall: x in [2, 2.4], y in [0, 3], z in [-3, 3] plus <paramref name="zOffset"/>.</summary>
        public void DrawWall(float zOffset = 0f) => Box(0.4f, 3f, 6f, new Vector3(2.2f, 1.5f, zOffset));

        /// <summary>The same wall split in two, leaving z in [-0.5, 0.5] open.</summary>
        public void DrawSplitWall()
        {
            Box(0.4f, 3f, 2.5f, new Vector3(2.2f, 1.5f, 1.75f));
            Box(0.4f, 3f, 2.5f, new Vector3(2.2f, 1.5f, -1.75f));
        }

        /// <summary>A narrow pillar standing well outside the framed floor, between the far light and the middle
        /// of the picture.</summary>
        public void DrawOffscreenPillar() => Box(0.6f, 3f, 0.4f, OffscreenPillarCentre);

        /// <summary>The lamp's own closed body around the flame: a 20 cm cube CENTRED on the light, so the
        /// nearest surface in every direction is the fixture itself, 10 cm away at the faces and 17.3 cm at the
        /// corners. Every placed lamp model is this shape.</summary>
        public void DrawLampFixture(Vector3 centre) => Box(0.2f, 0.2f, 0.2f, centre);

        /// <summary>
        /// The same lamp with its BRACKET, as one mesh: a 20 cm column running from 60 cm below the flame to 10 cm
        /// above it, so the flame is closed in on every side and the piece as a whole reaches well past any
        /// sensible clearance.
        /// <para>
        /// That second half is why the cases use this rather than the bare cube. A bare cube lies entirely inside
        /// a 25 cm clearance, so the instance is dropped by the CPU sphere test and the fragment's own discard is
        /// never asked anything. Real fixtures hang off brackets and stand on posts, and the geometry that goes
        /// dark is only the part near the flame.
        /// </para>
        /// </summary>
        public void DrawLampOnItsBracket(Vector3 flame) =>
            Box(0.2f, 1.2f, 0.2f, flame - new Vector3(0f, 0.5f, 0f));

        /// <summary>Queue a point light with a shadow request.</summary>
        public void AddLight(Vector3 at, Color colour, float radius, float intensity, LightShadow shadow)
            => scene.AddLight(at, colour, radius, intensity, shadow);

        /// <summary>Queue a point light through the four-argument overload, which is the unshadowed path.</summary>
        public void AddLight(Vector3 at, Color colour, float radius, float intensity)
            => scene.AddLight(at, colour, radius, intensity);

        /// <summary>One box caster, <paramref name="sx"/> by <paramref name="sy"/> by <paramref name="sz"/> metres
        /// at <paramref name="centre"/>, white unless <paramref name="colour"/> says otherwise. Public so a
        /// sibling class can assemble a fixture of its own out of the same shared scene, and so a case that needs
        /// to tell one surface from another in the picture can give it its own albedo.</summary>
        public void Box(float sx, float sy, float sz, Vector3 centre, Color? colour = null) =>
            scene.Draw(box, Matrix4x4.CreateScale(sx, sy, sz) * Matrix4x4.CreateTranslation(centre),
                colour ?? new Color(1f, 1f, 1f, 1f));
    }

    /// <summary>Render ONE frame of <paramref name="describe"/> and read it back, under
    /// <paramref name="budget"/> (the shipped defaults when null).</summary>
    public Shot One(Action<Frame> describe, PointShadowSettings? budget = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        return Many(1, (f, _) => describe(f), budget)[0];
    }

    /// <summary>What the point-shadow atlas is live at, for the cases that change the budget.</summary>
    public PointShadowResolution Resolved
    {
        get
        {
            Device();
            return _scene!.ResolvedPointShadows;
        }
    }

    /// <summary>Where the camera stands, so a case that depends on which of two lights the budget ranks first can
    /// say so as a precondition rather than assume it.</summary>
    public Vector3 Eye
    {
        get
        {
            Device();
            return _scene!.Camera.Eye;
        }
    }

    /// <summary>Whether an atlas is allocated at all, which is what the released case asserts.</summary>
    public bool HasAtlas
    {
        get
        {
            Device();
            return _scene!.PointShadowTexture is not null;
        }
    }

    /// <summary>
    /// Render <paramref name="frames"/> consecutive frames of the same scene through ONE <see cref="Scene3D"/>
    /// and read every one of them back, which is how the cache cases watch a row go from rebuilt to reused.
    /// <paramref name="describe"/> receives the frame index.
    /// <para>
    /// EVERY CAPTURE STARTS COLD AND BURNS TWO FRAMES GETTING THERE, because allocating, reshaping and releasing
    /// the atlas is frame-BOUNDARY work. The first throwaway frame turns point shadows off, which gives the atlas
    /// back, so the scene the whole class shares cannot hand one case the layout and the cached rows another case
    /// left behind. The second is the WARM-UP: it asks for <paramref name="budget"/> with no atlas standing, so
    /// it renders unshadowed and the boundary after it brings the atlas up. What the cases then see is frame 1 of
    /// a fresh atlas, every time and in any order, which is what makes a rebuild counter mean something.
    /// </para>
    /// </summary>
    public IReadOnlyList<Shot> Many(int frames, Action<Frame, int> describe, PointShadowSettings? budget = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        IGpuDevice gd = Device();
        Scene3D scene = _scene!;
        var frame = new Frame(scene, _floor, _box);

        scene.RequestPointShadowSettings(new PointShadowSettings { Enabled = false });
        RenderFrame();                                                  // gives the atlas back
        scene.RequestPointShadowSettings(budget ?? new PointShadowSettings());
        Capture(0);                                                     // the warm-up, which allocates it again

        var shots = new List<Shot>(frames);
        for (int i = 0; i < frames; i++) shots.Add(Capture(i));
        return shots;

        Shot Capture(int index)
        {
            scene.Begin();
            describe(frame, index);
            scene.PrepareFrame();
            using (GpuRecording.Open(gd, _commands!, "PointShadowScene.Capture"))
                scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
            gd.Submit(_commands!);
            gd.WaitForIdle();
            return new Shot(GpuReadback.ToRgba(gd, _target!, Width, Height),
                scene.LastShadowPassDiagnostics, scene.PointShadowedLights, scene.Camera);
        }

        // An empty frame, which is all it takes to reach a boundary. Nothing is drawn and nothing is read back:
        // its whole job is to let the release land.
        void RenderFrame()
        {
            scene.Begin();
            scene.PrepareFrame();
            using (GpuRecording.Open(gd, _commands!, "PointShadowScene.Release"))
                scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
            gd.Submit(_commands!);
            gd.WaitForIdle();
        }
    }

    /// <summary>Whether a world point projects inside the picture, which is how the off-camera case states that
    /// its caster really is off camera rather than merely far away.</summary>
    public bool OnScreen(Vector3 world)
    {
        Device();
        _scene!.Camera.WorldToScreen(world, Width, Height, out Vector2 px);
        return px.X >= 0f && px.X < Width && px.Y >= 0f && px.Y < Height;
    }

    IGpuDevice Device()
    {
        if (_gpu is not null) return _gpu.GpuDevice;

        _gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = _gpu.GpuDevice;
        IGpuResourceFactory factory = gd.Factory;
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D(
            Width, Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _commands = factory.CreateCommandList();
        _scene = new Scene3D(gd, _framebuffer.Outputs, null);
        _floor = _scene.LoadMesh(MeshPrimitives.Plane(40f, 40f));
        _box = _scene.LoadMesh(MeshPrimitives.Box(1f));
        Setup(_scene);
        return gd;
    }

    /// <summary>The framing and the lighting environment, applied once. Almost straight down (the camera is an
    /// orthographic iso one, and at this elevation a floor point's world x and z map to picture x and y), and
    /// every global light term off, so the picture is the point lights and nothing else.</summary>
    static void Setup(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.TransparentBackground = false;
        scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.LightColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.AmbientColor = new Color(0f, 0f, 0f, 1f);
        scene.Post.CelBands = 0;                        // banded ndl would quantise every probe level
        // ACES filmic tone mapping is channel-COUPLED (it preserves chroma across the three), so dimming one
        // light's contribution moves the other two channels with it. The slot-order case reads red and blue
        // separately, so the operator has to be off for those two numbers to mean one light each.
        scene.Post.Hdr.Enabled = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;   // the key light is not what this class measures
        scene.EffectTimeSeconds = 0f;
        scene.Camera.AspectRatio = (float)Width / Height;
        scene.Camera.Azimuth = 0f;
        scene.Camera.Elevation = 1.35f;                 // about 77 degrees: nearly plan view, minimal occlusion
        scene.Camera.Frame(Vector3.Zero, new Vector3(22f, 2f, 22f), margin: 1f);
    }

    public void Dispose()
    {
        _commands?.Dispose();
        _scene?.Dispose();
        _framebuffer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
        _commands = null;
        _scene = null;
        _framebuffer = null;
        _target = null;
        _gpu = null;
    }
}
