using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The FFT ocean's sampling frame, headless: the onshore-focus rotation, the per-cascade rotation offsets and
    /// the large-scale domain warp, against <see cref="OceanFocus"/> - the CPU mirror the two shader stages are
    /// written from.
    /// <para>
    /// The frame is the whole feature, and none of it is observable in the produced maps (they are unchanged), so
    /// this is where it gets proved. Three properties matter and each has its own test below: the default is the
    /// EXACT identity, a rotation preserves the LENGTH of everything it turns (so normals stay unit and the
    /// Toksvig variance is untouched), and at full strength the sea genuinely converges on the focus point from
    /// every azimuth rather than merely being disturbed near it.
    /// </para>
    /// </summary>
    public sealed class OceanFocusTests
    {
        const float Wind = 30f * MathF.PI / 180f;
        static readonly Vector2 Focus = new(120f, -45f);

        static Vector2 WindVector(float radians) => new(MathF.Cos(radians), MathF.Sin(radians));

        static float Angle(Vector2 v) => MathF.Atan2(v.Y, v.X);

        /// <summary>Signed difference between two headings, wrapped to (-pi, pi].</summary>
        static float AngleDelta(float a, float b)
        {
            float d = a - b;
            return MathF.Atan2(MathF.Sin(d), MathF.Cos(d));
        }

        /// <summary>A ring of sample positions all round the focus point, so "from every azimuth" is actually
        /// tested from every azimuth rather than from the one the author happened to pick.</summary>
        static Vector2[] RingAround(Vector2 centre, float radius, int count)
        {
            var ring = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                float a = i * 2f * MathF.PI / count;
                ring[i] = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            }
            return ring;
        }

        // ---- The default is the exact identity ----------------------------------------------------------------

        [Fact]
        public void StrengthZeroIsTheExactIdentityAndTurnsNothingAtAll()
        {
            // Bit-exact, not within a tolerance, and that is the point of the whole knob group: every default here
            // has to leave the surface byte-identical to the ocean that shipped before the sampling frame existed,
            // which is what lets this release skip a golden bake. FocusRotation reaches it by an early return
            // rather than by evaluating cos(0), because GLSL allows cos a couple of ULP and a backend answering
            // 0.99999994 would scale every sample position by that.
            foreach (Vector2 p in RingAround(Focus, 260f, 16))
            {
                Vector2 cs = OceanFocus.FocusRotation(p, Focus, 0f, Wind);
                Assert.Equal(1f, cs.X);
                Assert.Equal(0f, cs.Y);

                Assert.Equal(p.X, OceanFocus.ToSampleFrame(p, cs).X);
                Assert.Equal(p.Y, OceanFocus.ToSampleFrame(p, cs).Y);
                Assert.Equal(p.X, OceanFocus.ToWorldFrame(p, cs).X);
                Assert.Equal(p.Y, OceanFocus.ToWorldFrame(p, cs).Y);
            }
        }

        [Fact]
        public void ComposingTwoIdentitiesIsExactlyTheIdentity()
        {
            // The per-cascade offsets compose onto the focus rotation, so the all-default case runs through
            // Compose on every cascade of every vertex and every fragment. It has to come out exact there too.
            Vector2 cs = OceanFocus.Compose(OceanFocus.Identity, OceanFocus.Identity);
            Assert.Equal(1f, cs.X);
            Assert.Equal(0f, cs.Y);
        }

        [Fact]
        public void ARenormalizedUnitPairIsReturnedUntouched()
        {
            // The fragment renormalizes the interpolated pair, so this runs in the default case as well. Returning
            // the input UNCHANGED (rather than dividing it by a computed 1.0) is what keeps that exact: neither
            // hardware interpolation of a constant attribute nor inversesqrt(1.0) is promised to be exact.
            Vector2 kept = OceanFocus.Renormalize(OceanFocus.Identity);
            Assert.Equal(1f, kept.X);
            Assert.Equal(0f, kept.Y);

            // A chord (what interpolation across a triangle actually produces) is scaled back up.
            var chord = new Vector2(0.6f, 0.6f);
            Vector2 fixedUp = OceanFocus.Renormalize(chord);
            Assert.Equal(1f, fixedUp.Length(), 5);
            Assert.Equal(Angle(chord), Angle(fixedUp), 5);

            // Degenerate input (the two ends of a triangle edge a half turn apart) falls back rather than dividing
            // by zero.
            Assert.Equal(OceanFocus.Identity, OceanFocus.Renormalize(Vector2.Zero));
        }

        [Fact]
        public void AZeroWarpReturnsThePositionExactly()
        {
            foreach (Vector2 p in RingAround(Vector2.Zero, 900f, 12))
            {
                Vector2 w = OceanFocus.DomainWarp(p, 0f, 1250f);
                Assert.Equal(p.X, w.X);
                Assert.Equal(p.Y, w.Y);
            }
        }

        // ---- The convergence claim ----------------------------------------------------------------------------

        [Fact]
        public void AtFullStrengthTheWavesRunAtTheFocusPointFromEveryAzimuth()
        {
            // This IS the feature. The spectrum's dominant heading is WindDirectionDegrees in the sampling frame,
            // so rotating that heading into the world frame has to give the direction from the sample position
            // toward the focus point - from all round it, not just downwind of it.
            Vector2 heading = WindVector(Wind);
            foreach (float radius in new[] { 8f, 60f, 400f, 3000f })
            {
                foreach (Vector2 p in RingAround(Focus, radius, 24))
                {
                    Vector2 cs = OceanFocus.FocusRotation(p, Focus, 1f, Wind);
                    Vector2 travel = OceanFocus.ToWorldFrame(heading, cs);
                    Vector2 want = Vector2.Normalize(Focus - p);
                    Assert.Equal(0f, AngleDelta(Angle(travel), Angle(want)), 4);
                }
            }
        }

        [Fact]
        public void APartialStrengthTurnsThatFractionOfTheWay()
        {
            // Strength is a fraction of the SHORTEST-way-round turn, so the resulting heading sits that fraction
            // of the way from the wind heading to the focus heading.
            Vector2 heading = WindVector(Wind);
            var p = new Vector2(-300f, 210f);
            float full = AngleDelta(Angle(Focus - p), Wind);

            foreach (float strength in new[] { 0.25f, 0.5f, 0.75f, 1f })
            {
                Vector2 cs = OceanFocus.FocusRotation(p, Focus, strength, Wind);
                Vector2 travel = OceanFocus.ToWorldFrame(heading, cs);
                Assert.Equal(strength * full, AngleDelta(Angle(travel), Wind), 4);
            }
        }

        [Fact]
        public void TheUnavoidableSeamOfAPartialFocusSitsOnTheDownwindRayAndClosesAtFullStrength()
        {
            // A uniform heading field wraps zero times around the focus point and a converging one wraps once, so
            // no continuous blend between them exists and a partial focus MUST leave a discontinuity somewhere.
            // This pins where: the ray running from the focus point in the direction the wind blows, which is
            // where the shortest-way-round turn flips between a half turn each way. The knob's doc promises that
            // location (so a consumer can aim it at land with WindDirectionDegrees) and promises it closes at
            // strength 1, and neither should be able to move without this failing.
            Vector2 downwind = WindVector(Wind);
            Vector2 across = new(-downwind.Y, downwind.X);
            Vector2 onRay = Focus + downwind * 500f;

            foreach (float epsilon in new[] { 1e-2f, 1e-3f })
            {
                Vector2 left = onRay + across * epsilon;
                Vector2 right = onRay - across * epsilon;

                // Half strength: the frame jumps by a half turn either way across the ray, i.e. a full turn of gap.
                Vector2 a = OceanFocus.FocusRotation(left, Focus, 0.5f, Wind);
                Vector2 b = OceanFocus.FocusRotation(right, Focus, 0.5f, Wind);
                Assert.Equal(MathF.PI, MathF.Abs(AngleDelta(Angle(a), Angle(b))), 2);

                // Full strength: a whole turn IS no turn, so the two sides agree and there is no seam left.
                Vector2 c = OceanFocus.FocusRotation(left, Focus, 1f, Wind);
                Vector2 d = OceanFocus.FocusRotation(right, Focus, 1f, Wind);
                Assert.Equal(0f, AngleDelta(Angle(c), Angle(d)), 4);
            }

            // And away from the ray the field is smooth at any strength, so the seam is one ray rather than a
            // general roughness that happens to be worst there.
            Vector2 offRay = Focus + across * 500f;
            Vector2 e = OceanFocus.FocusRotation(offRay + across * 1e-3f, Focus, 0.5f, Wind);
            Vector2 f = OceanFocus.FocusRotation(offRay - across * 1e-3f, Focus, 0.5f, Wind);
            Assert.Equal(0f, AngleDelta(Angle(e), Angle(f)), 4);
        }

        [Fact]
        public void TheFrameIsFiniteEverywhereIncludingAtTheFocusPointItself()
        {
            // atan2(0, 0) is undefined in GLSL, and the angular gradient of the focus field is unbounded as the
            // distance goes to zero. The clamp keeps a sample AT the point (a plane drawn over the island the
            // focus is aimed at) from poisoning a vertex with a NaN, which would take the whole triangle with it.
            foreach (float d in new[] { 0f, 1e-6f, 1e-4f, 1e-2f, 1f })
            {
                Vector2 cs = OceanFocus.FocusRotation(Focus + new Vector2(d, 0f), Focus, 1f, Wind);
                Assert.True(float.IsFinite(cs.X) && float.IsFinite(cs.Y), $"non-finite frame at distance {d}");
                Assert.Equal(1f, cs.Length(), 4);
            }
            Assert.Equal(OceanFocus.Identity, OceanFocus.FocusRotation(Focus, Focus, 1f, Wind));
        }

        // ---- Frame algebra ------------------------------------------------------------------------------------

        [Fact]
        public void RotatingAVectorPreservesItsLengthSoNormalsStayUnit()
        {
            // The fragment rotates the sampled height slope back into the world frame before building the normal.
            // If that rotation were not a rotation - a chord left unnormalized, a transposed sign - the slope
            // magnitude would move with the heading, and both the normal and the Toksvig variance the glint lobe
            // receives would drift with it.
            var slope = new Vector2(0.37f, -0.82f);
            for (int deg = -180; deg <= 180; deg += 7)
            {
                float rad = deg * MathF.PI / 180f;
                var cs = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                Assert.Equal(slope.Length(), OceanFocus.ToWorldFrame(slope, cs).Length(), 5);
                Assert.Equal(slope.Length(), OceanFocus.ToSampleFrame(slope, cs).Length(), 5);
            }
        }

        [Fact]
        public void ToWorldFrameInvertsToSampleFrame()
        {
            // The two halves of the round trip: a position goes INTO the cascade's frame to index the map, and the
            // vector that comes back out goes the other way. Getting one of the two transposed is the classic way
            // to ship a surface whose lighting runs at an angle to its geometry, and it looks plausible enough to
            // survive an eyeball.
            var v = new Vector2(13.5f, -4.25f);
            for (int deg = -180; deg <= 180; deg += 11)
            {
                float rad = deg * MathF.PI / 180f;
                var cs = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                Vector2 round = OceanFocus.ToWorldFrame(OceanFocus.ToSampleFrame(v, cs), cs);
                Assert.Equal(v.X, round.X, 4);
                Assert.Equal(v.Y, round.Y, 4);
            }
        }

        [Fact]
        public void ComposeAddsTheAngles()
        {
            foreach ((float a, float b) in new[] { (0f, 19f), (19f, 37f), (-140f, 200f), (170f, 170f) })
            {
                float ra = a * MathF.PI / 180f, rb = b * MathF.PI / 180f;
                Vector2 composed = OceanFocus.Compose(
                    new Vector2(MathF.Cos(ra), MathF.Sin(ra)),
                    new Vector2(MathF.Cos(rb), MathF.Sin(rb)));
                Assert.Equal(0f, AngleDelta(Angle(composed), ra + rb), 4);
                Assert.Equal(1f, composed.Length(), 5);
            }
        }

        [Fact]
        public void PerCascadeOffsetsLeaveTheCascadesTurnedRelativeToEachOtherUnderAnyFocus()
        {
            // The de-tiling claim: the offsets are FIXED relative angles between the cascade lattices, and the
            // focus rotation turns all three together rather than pulling them apart. So the decorrelation the
            // offsets buy survives wherever the focus points, which is what stops the sea reading as three
            // lattices that agree in some places and not others.
            var offsets = new[] { 0f, 19f, 37f };
            foreach (Vector2 p in RingAround(Focus, 300f, 8))
            {
                Vector2 focusRot = OceanFocus.FocusRotation(p, Focus, 1f, Wind);
                var frames = new Vector2[offsets.Length];
                for (int i = 0; i < offsets.Length; i++)
                {
                    float rad = offsets[i] * MathF.PI / 180f;
                    frames[i] = OceanFocus.Compose(focusRot, new Vector2(MathF.Cos(rad), MathF.Sin(rad)));
                }
                for (int i = 1; i < offsets.Length; i++)
                    Assert.Equal((offsets[i] - offsets[0]) * MathF.PI / 180f,
                        AngleDelta(Angle(frames[i]), Angle(frames[0])), 4);
            }
        }

        // ---- The sector blend that carries the heading ---------------------------------------------------------

        [Fact]
        public void AnUnrotatedFrameIsOneTapAtFullWeight()
        {
            // The identity blend, and it has to be exact for the same reason everything else here does. The shader
            // reaches it WITHOUT running the quantization at all (an early return on the strength), because the
            // quantization goes through atan, floor and cos, and none of those is promised exact at zero.
            (Vector2 lower, float t, float norm) = OceanFocus.NoSectors;
            Assert.Equal(1f, lower.X);
            Assert.Equal(0f, lower.Y);
            Assert.Equal(0f, t);
            Assert.Equal(1f, norm);
            // The weights the shader derives from that triple: full on the lower tap, nothing on the upper, both
            // exactly, so the upper tap branches out and the lower multiplies by a literal 1.
            Assert.Equal(1f, (1f - t) * norm);
            Assert.Equal(0f, t * norm);
        }

        [Fact]
        public void TheTwoTapsStraddleTheWantedHeadingWithinOneSector()
        {
            // The claim the whole design rests on: the wanted heading is always between the two taps, and never
            // further than a sector from either. That is what makes the mix read as directional SPREAD around the
            // right heading rather than as the wrong heading.
            foreach (int sectors in new[] { 4, 8, 12, 36, 64 })
            {
                float step = 2f * MathF.PI / sectors;
                for (int deg = -180; deg <= 180; deg += 3)
                {
                    float phi = deg * MathF.PI / 180f;
                    var want = new Vector2(MathF.Cos(phi), MathF.Sin(phi));
                    (Vector2 lower, float t, _) = OceanFocus.Sectors(want, sectors);
                    Vector2 upper = OceanFocus.Compose(lower, new Vector2(MathF.Cos(step), MathF.Sin(step)));

                    Assert.InRange(t, 0f, 1f);
                    float below = AngleDelta(phi, Angle(lower));
                    float above = AngleDelta(Angle(upper), phi);
                    Assert.InRange(below, -1e-4f, step + 1e-4f);
                    Assert.InRange(above, -1e-4f, step + 1e-4f);
                    // t places the wanted heading between them proportionally, which is what the weights read.
                    Assert.Equal(t * step, below, 4);
                }
            }
        }

        [Fact]
        public void TheBlendWeightsConserveTheSpectrumsVariance()
        {
            // Displacement and slope are zero-mean Gaussian fields, so mixing two DECORRELATED realizations with
            // weights (a, b) gives a field of variance a^2 + b^2. Plain linear weights would therefore dip the
            // wave height to sqrt(0.5) - about 30 per cent short - halfway through every sector, as a visible ring
            // of calm. L2 weights are what remove that, and this pins them at exactly unit power everywhere.
            for (float t = 0f; t <= 1f; t += 1f / 64f)
            {
                float norm = 1f / MathF.Sqrt(MathF.Max((1f - t) * (1f - t) + t * t, 1e-8f));
                float a = (1f - t) * norm, b = t * norm;
                Assert.Equal(1f, a * a + b * b, 5);
            }

            // And the same triple as the shader gets it, straight off Sectors.
            foreach (int deg in new[] { -173, -40, 0, 17, 96, 179 })
            {
                float phi = deg * MathF.PI / 180f;
                (_, float t, float norm) = OceanFocus.Sectors(new Vector2(MathF.Cos(phi), MathF.Sin(phi)), 12);
                float a = (1f - t) * norm, b = t * norm;
                Assert.Equal(1f, a * a + b * b, 5);
                // Foam takes the LINEAR pair instead, because it is a bounded coverage rather than a Gaussian
                // field and what has to be preserved there is its mean.
                Assert.Equal(1f, (1f - t) + t, 5);
            }
        }

        [Fact]
        public void EverySectorTapIsAnExactRotationSoNoTapIsADistortedField()
        {
            // The reason this design exists rather than the obvious one: each tap must be a CONSTANT rotation, so
            // each sampled field is undistorted. A tap whose pair drifted off unit length would scale the sample
            // position, which is exactly the smear that sinks the rotate-the-coordinate approach.
            foreach (int sectors in new[] { 4, 12, 64 })
                for (int deg = -180; deg <= 180; deg += 5)
                {
                    float phi = deg * MathF.PI / 180f;
                    (Vector2 lower, _, _) = OceanFocus.Sectors(new Vector2(MathF.Cos(phi), MathF.Sin(phi)), sectors);
                    Assert.Equal(1f, lower.Length(), 5);
                    // And it IS a lattice rotation: a whole number of sectors round.
                    float k = Angle(lower) * sectors / (2f * MathF.PI);
                    Assert.Equal(MathF.Round(k), k, 3);
                }
        }

        [Fact]
        public void TheSectorCountIsClampedRatherThanTrusted()
        {
            // It is a public settings-bag int, so 0 and 1000 both arrive eventually. 0 would divide by zero.
            Assert.Equal(OceanFocus.Sectors(new Vector2(0f, 1f), OceanFocus.MinSectors),
                         OceanFocus.Sectors(new Vector2(0f, 1f), 0));
            Assert.Equal(OceanFocus.Sectors(new Vector2(0f, 1f), OceanFocus.MaxSectors),
                         OceanFocus.Sectors(new Vector2(0f, 1f), 100000));
        }

        [Fact]
        public void AtFullStrengthTheBlendedHeadingStillPointsAtTheFocus()
        {
            // End to end, and the assertion the feature is actually judged on. The two taps carry the sea, so the
            // heading the surface reads as is their weighted mean direction, and that has to be the direction to
            // the focus point - from every azimuth, at the default sector count.
            Vector2 heading = WindVector(Wind);
            foreach (Vector2 p in RingAround(Focus, 350f, 32))
            {
                Vector2 wanted = OceanFocus.FocusRotation(p, Focus, 1f, Wind);
                (Vector2 lower, float t, _) = OceanFocus.Sectors(wanted, 12);
                float step = 2f * MathF.PI / 12f;
                Vector2 upper = OceanFocus.Compose(lower, new Vector2(MathF.Cos(step), MathF.Sin(step)));

                Vector2 travel = OceanFocus.ToWorldFrame(heading, lower) * (1f - t)
                               + OceanFocus.ToWorldFrame(heading, upper) * t;
                Vector2 want = Vector2.Normalize(Focus - p);
                // Within half a sector: the mean of two headings a sector apart, weighted to straddle the wanted
                // one, lands on it to well inside the spectrum's own directional lobe.
                Assert.InRange(MathF.Abs(AngleDelta(Angle(travel), Angle(want))), 0f, 0.5f * step);
            }
        }

        // ---- Domain warp --------------------------------------------------------------------------------------

        [Fact]
        public void TheWarpStaysInsideItsStatedAmplitudeAndIsStaticInWorldSpace()
        {
            const float amp = 30f, lambda = 1250f;
            float peak = 0f;
            for (float x = -4000f; x <= 4000f; x += 37f)
            {
                for (float z = -4000f; z <= 4000f; z += 41f)
                {
                    var p = new Vector2(x, z);
                    Vector2 w = OceanFocus.DomainWarp(p, amp, lambda);
                    peak = MathF.Max(peak, (w - p).Length());
                    // Static: the same position warps to the same place every time it is asked, with no clock in
                    // the signature at all. A drifting warp at this wavelength reads as the whole sea sloshing.
                    Assert.Equal(w, OceanFocus.DomainWarp(p, amp, lambda));
                }
            }
            // The lobe peaks at 1 + 0.7 on each axis and is divided by that, so the amplitude knob is in metres of
            // actual displacement rather than in units of whatever the lobe happens to sum to.
            Assert.InRange(peak, 0.5f * amp, 1.45f * amp);
        }

        [Fact]
        public void TheWarpsLocalStretchMatchesTheRatioTheKnobDocuments()
        {
            // WaterSeaState.DomainWarpMetres tells the consumer to keep 2*pi*amplitude/wavelength well under 1,
            // and that the surface tears past 1. That is a claim about the domain's Jacobian, so measure it: the
            // worst singular value of d(warp)/d(position) has to track the stated ratio.
            const float lambda = 1250f;
            foreach (float amp in new[] { 10f, 30f, 60f })
            {
                float stated = 2f * MathF.PI * amp / lambda;
                float worst = 0f;
                const float h = 0.05f;
                for (float x = -1500f; x <= 1500f; x += 53f)
                {
                    for (float z = -1500f; z <= 1500f; z += 59f)
                    {
                        var p = new Vector2(x, z);
                        Vector2 dX = (OceanFocus.DomainWarp(p + new Vector2(h, 0f), amp, lambda)
                                    - OceanFocus.DomainWarp(p - new Vector2(h, 0f), amp, lambda)) / (2f * h);
                        Vector2 dZ = (OceanFocus.DomainWarp(p + new Vector2(0f, h), amp, lambda)
                                    - OceanFocus.DomainWarp(p - new Vector2(0f, h), amp, lambda)) / (2f * h);
                        worst = MathF.Max(worst, MathF.Max(
                            MathF.Abs(dX.X - 1f) + MathF.Abs(dX.Y),
                            MathF.Abs(dZ.Y - 1f) + MathF.Abs(dZ.X)));
                    }
                }
                Assert.InRange(worst, 0.4f * stated, 1.2f * stated);
            }
        }

        [Fact]
        public void TheChainWarpsBeforeItRotates()
        {
            // Order is load-bearing and stated in the knob docs: the warp bends world space, then the rotations
            // read the bent space. Rotating first would turn the warp's own pattern with the focus, which would
            // put the de-tiler's structure into the focus field instead of into the world.
            var p = new Vector2(410f, -230f);
            Vector2 focusRot = OceanFocus.FocusRotation(p, Focus, 1f, Wind);
            var cascadeRot = new Vector2(MathF.Cos(0.3f), MathF.Sin(0.3f));

            (Vector2 sample, Vector2 cs) = OceanFocus.SampleFrame(p, focusRot, cascadeRot, 30f, 1250f);
            Vector2 expected = OceanFocus.ToSampleFrame(OceanFocus.DomainWarp(p, 30f, 1250f),
                OceanFocus.Compose(focusRot, cascadeRot));

            Assert.Equal(expected.X, sample.X, 4);
            Assert.Equal(expected.Y, sample.Y, 4);
            Assert.Equal(OceanFocus.Compose(focusRot, cascadeRot), cs);
        }

        // ---- Mirror drift -------------------------------------------------------------------------------------

        [Fact]
        public void BothShaderStagesCarryTheSameSamplingFrameHelpersAndLiterals()
        {
            // The GPU has the only copy that ships, so a mirror that drifts from it is worse than no mirror: every
            // test above would keep passing while the surface did something else. Guard the helper names and the
            // literals in both stages (the block is spliced into each), the same way UboLayoutTests guards the
            // UBO members.
            foreach (string token in new[]
            {
                "vec2 oceanRotAdd(", "vec2 oceanToSample(", "vec2 oceanToWorld(", "vec2 oceanUnitRot(",
                "vec2 oceanFocusRot(", "vec2 oceanWarp(", "vec4 oceanSectors(",
                "KE_FOCUS_MIN_D2 = 1e-8", "KE_FOCUS_UNIT_TOL = 1e-6",
                "KE_WARP_PEAK = 1.7", "KE_WARP_FREQ_B = 0.57",
            })
            {
                Assert.True(KhaozEngine.Render3D.Internal.ShaderSources.WaterVert.Contains(token),
                    $"WaterVert lost '{token}': the sampling frame drifted from OceanFocus. Fix ShaderSources.WaterFft.cs or the mirror.");
                Assert.True(KhaozEngine.Render3D.Internal.ShaderSources.WaterFrag.Contains(token),
                    $"WaterFrag lost '{token}': the sampling frame drifted from OceanFocus. Fix ShaderSources.WaterFft.cs or the mirror.");
            }

            Assert.Equal(1e-8f, OceanFocus.MinFocusDistanceSquared);
            Assert.Equal(1e-6f, OceanFocus.UnitTolerance);
            Assert.Equal(1.7f, OceanFocus.WarpPeak);
            Assert.Equal(0.57f, OceanFocus.WarpFrequencyB);
        }
    }
}
