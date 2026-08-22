using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Row 3 of the Veldrid removal (<c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> section 3,
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/686">#686</see>): the three native-owned golden
    /// families are byte-identical COPIES of the three incumbent families, and this is where that is asserted
    /// rather than assumed.
    /// <para>
    /// WHY A COPY AND NOT A BAKE. Every guest leg was green on the day row 2 landed, which is exactly the
    /// statement that each native backend already reproduces its host family's committed grids on the same
    /// rasterizer. Baking the natives fresh would have replaced that agreement with each implementation agreeing
    /// with itself, and a self-baked golden always passes itself. So the operation was a copy, and the copy is
    /// the one moment in this program where that trap is avoidable for free.
    /// </para>
    /// <para>
    /// <b>THIS CLASS IS MEANT TO BE DELETED IN ROW 4</b>, together with the incumbent backends and the three
    /// incumbent families it reads. It has nothing to say once <c>*.metal.txt</c>, <c>*.vulkan.txt</c> and
    /// <c>*.direct3d11.txt</c> are gone, and leaving it behind would leave a test that reds on a deletion the
    /// program planned.
    /// </para>
    /// <para>
    /// UNTIL THEN IT IS LOAD-BEARING, AND IT CONSTRAINS THE BAKE. Row 2 gave the native legs the right to bake,
    /// because a family's owner is the thing that may re-bake it and row 4 leaves no other owner alive. The
    /// mechanism is therefore correct from <c>17.40.0</c>, and what is constrained in this window is the
    /// OPERATOR: a new scene added before row 4 is baked on the three incumbent legs and COPIED across, and a
    /// native leg's bake artifacts are not committed. Baking all six and committing both halves would fork each
    /// pair on its first grid and end the copy invariant, which is the guest-era agreement between two
    /// implementations expressed as committed bytes. That is what this row reds on, with the message below
    /// naming both ways out.
    /// </para>
    /// <para>
    /// The name carries "Golden" on purpose. <c>cross-platform-gpu.yml</c> selects the push path with
    /// <c>--filter FullyQualifiedName~Golden</c>, and this row wants to run on every leg: it is device-free, so
    /// it costs nothing there and it makes the copy claim a per-leg gate rather than a claim checked once on the
    /// machine that made the copy.
    /// </para>
    /// </summary>
    public class GoldenFamilyCopyGoldenTests
    {
        /// <summary>
        /// The env var whose presence makes this row unanswerable, and the reason the two attributes below
        /// exist. A bake run REWRITES the files this class reads, in the checkout, while xUnit is running it in
        /// parallel with the golden tests doing the writing, so it would compare a half-replaced family against a
        /// fully replaced one and red on the race rather than on anything true. Reading it at discovery is
        /// enough: the variable is set by the workflow before the test host starts and nothing in the process
        /// changes it.
        /// <para>
        /// The DELIBERATE outcome is unaffected. Committing a native family's bake output in this window really
        /// does end the copy invariant, and the very next non-bake run says so with the message below, which is
        /// the signal this class is for. What is skipped is only the run that cannot answer the question.
        /// </para>
        /// </summary>
        internal const string BakeEnvVar = "KE_UPDATE_GOLDENS";

        internal static string? BakeSkipReason(string? updateGoldens)
            => updateGoldens == "1"
                ? "KE_UPDATE_GOLDENS=1: this is a bake run, which rewrites the committed grids this row reads "
                    + "while it reads them. The copy invariant is checked on every ordinary run instead."
                : null;

        sealed class NotWhileBakingFactAttribute : FactAttribute
        {
            public NotWhileBakingFactAttribute()
            {
                string? reason = BakeSkipReason(Environment.GetEnvironmentVariable(BakeEnvVar));
                if (reason != null) Skip = reason;
            }
        }

        sealed class NotWhileBakingTheoryAttribute : TheoryAttribute
        {
            public NotWhileBakingTheoryAttribute()
            {
                string? reason = BakeSkipReason(Environment.GetEnvironmentVariable(BakeEnvVar));
                if (reason != null) Skip = reason;
            }
        }

        /// <summary>The count at the time row 3 landed: 40 grids in each of the three incumbent families.</summary>
        const int PairsAtRowThree = 120;

        /// <summary>The three (incumbent family, native family) pairs, as they appear in a golden filename.</summary>
        public static TheoryData<string, string> Families => new()
        {
            { "metal", "metal-native" },
            { "vulkan", "vulkan-native" },
            { "direct3d11", "direct3d11-native" },
        };

        [NotWhileBakingTheory]
        [MemberData(nameof(Families))]
        public void EveryNativeFamilyGrid_IsAByteIdenticalCopyOfItsIncumbent(string incumbent, string native)
        {
            List<string> incumbentScenes = ScenesIn(incumbent);
            List<string> nativeScenes = ScenesIn(native);

            // A sweep that finds nothing reports clean, so the floor is asserted before the comparison is.
            Assert.True(incumbentScenes.Count > 0,
                $"no committed grids found for the '{incumbent}' family under {GoldenDir()}. This row compares "
                + "files, so an empty sweep is indistinguishable from a passing one.");

            Assert.Equal(incumbentScenes, nativeScenes);

            foreach (string scene in incumbentScenes)
            {
                byte[] want = File.ReadAllBytes(GridPath(scene, incumbent));
                byte[] got = File.ReadAllBytes(GridPath(scene, native));
                Assert.True(want.AsSpan().SequenceEqual(got),
                    $"golden '{scene}' differs between the '{incumbent}' family and its '{native}' copy. The "
                    + "native families were seeded as byte-identical copies so the guest-era agreement between "
                    + "two implementations survives as committed bytes. If this scene was re-baked on the native "
                    + "leg, that agreement is gone: re-bake on the incumbent and copy, or delete this class "
                    + "because row 4 has landed and the incumbent families are on their way out.");
            }
        }

        /// <summary>
        /// The whole sweep, counted. Row 3's gate is 120 grids and this is the row that says the number out loud,
        /// as a floor rather than an equality so that adding a scene AND copying it correctly stays green.
        /// </summary>
        [NotWhileBakingFact]
        public void TheThreeCopiedFamilies_CoverEveryCommittedGrid()
        {
            int pairs = ScenesIn("metal").Count + ScenesIn("vulkan").Count + ScenesIn("direct3d11").Count;

            Assert.True(pairs >= PairsAtRowThree,
                $"{pairs} incumbent grids found across the three families, fewer than the {PairsAtRowThree} row 3 "
                + "copied. Grids do not get deleted one at a time: either the goldens directory moved out from "
                + "under this test or a family was removed without removing this class with it.");
        }

        /// <summary>The scene names (the part before the family token) committed for <paramref name="family"/>, sorted.</summary>
        static List<string> ScenesIn(string family)
        {
            string suffix = "." + family + ".txt";
            return Directory.EnumerateFiles(GoldenDir(), "*" + suffix)
                .Select(Path.GetFileName)
                .Select(f => f![..^suffix.Length])
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        static string GridPath(string scene, string family)
            => Path.Combine(GoldenDir(), scene + "." + family + ".txt");

        /// <summary>
        /// The committed goldens directory next to this source file, resolved through
        /// <see cref="CallerFilePathAttribute"/> for the reason <c>GoldenCompare</c> does: it makes the path
        /// independent of the working directory and of the build output layout, so the test reads the committed
        /// source tree rather than a copy.
        /// </summary>
        static string GoldenDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "goldens");
    }
}
