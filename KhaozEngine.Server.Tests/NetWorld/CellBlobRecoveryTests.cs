using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The two RECOVERY paths a pre-v4 cell blob has, and the two ways the knobs meant to widen them used to narrow them
/// instead.
/// <para>
/// Both knobs are advertised as strictly helpful, so a server operator is pushed at them. Supplying
/// <see cref="CellBlobMigrationOptions.Registry"/> was the reverse for one whole class of save: a body carrying a
/// RETAINED unknown extension frame (an id the registry no longer registers, which retain-and-rewrite exists to carry
/// forward verbatim) had every candidate generation retired by that one rule, so a blob the same build migrates
/// cleanly WITHOUT a registry quarantined as corrupt WITH one. And
/// <see cref="CellBlobMigrationOptions.AssumedWireGeneration"/> is one knob serving two schema ranges: a long-lived
/// store holds both vintages, so a generation that resolves the v2 bodies must not refuse the v3 ones.
/// </para>
/// </summary>
public class CellBlobRecoveryTests
{
    private readonly ITestOutputHelper output;

    public CellBlobRecoveryTests(ITestOutputHelper output) => this.output = output;

    /// <summary>An extension id no registry in this test file registers: the retained-unknown-frame case.</summary>
    private const ushort DroppedExtensionId = ReplicationRegistry.FirstExtensionTypeId;

    private const int BodiesPerGeneration = 50;

    private static int Current => MoveProtocol.WireProtocolVersion;

    private enum Outcome { Migrated, Ambiguous, Refused, MisDecoded }

    /// <summary>
    /// The measured shape of the defect: fifty bodies at each wire generation a v2 blob can carry, every one of them
    /// a position frame, a movement frame and an extension frame at an id the supplied registry does not know.
    /// <para>
    /// Two runs over the same bodies, one with the registry and one without, because the claim the docs make about
    /// the knob is comparative: it may leave FEWER candidates standing (which turns an ambiguity into a migration),
    /// and it must never leave none where an unsupplied registry left one. A body that migrates without the registry
    /// and quarantines with it is the failure, and before the fix that was all 350 of them.
    /// </para>
    /// </summary>
    [Fact]
    public void RetainedUnknownExtension_SupplyingTheRegistry_NeverCostsABlobThatWouldHaveMigrated()
    {
        var withRegistry = new CellBlobMigrationOptions { Registry = MoveProtocol.CreateRegistry() };
        CellBlobMigrationOptions withoutRegistry = CellBlobMigrationOptions.None;

        var rng = new Random(20260819);
        var with = new Dictionary<Outcome, int>();
        var without = new Dictionary<Outcome, int>();
        var lost = new List<string>();
        int bodies = 0;

        for (int generation = PositionFrameBlobMigration.OldestAbsolutePositionWireGeneration;
             generation <= PositionFrameBlobMigration.NewestAbsolutePositionWireGeneration;
             generation++)
        {
            for (int i = 0; i < BodiesPerGeneration; i++, bodies++)
            {
                byte[] body = RetainedExtensionBody(rng, generation);
                byte[] expected = CellBlobRewriter.Rewrite(body, generation, Current, widenNetIds: false);

                Outcome a = Migrate(body, expected, withRegistry);
                Outcome b = Migrate(body, expected, withoutRegistry);
                Tally(with, a);
                Tally(without, b);
                if (b == Outcome.Migrated && a != Outcome.Migrated)
                    lost.Add($"generation {generation} body {i}: {b} without the registry, {a} with it");
            }
        }

        output.WriteLine($"{bodies} v2 bodies carrying a retained unknown extension frame");
        output.WriteLine($"  registry supplied: {Describe(with)}");
        output.WriteLine($"  no registry:       {Describe(without)}");

        Assert.Equal(0, Count(with, Outcome.MisDecoded));
        Assert.Equal(0, Count(without, Outcome.MisDecoded));
        Assert.Equal(0, Count(with, Outcome.Refused));
        Assert.True(lost.Count == 0,
            $"{lost.Count} of {bodies} bodies were lost by supplying the registry: " +
            string.Join(" | ", lost.Count > 5 ? lost.GetRange(0, 5) : lost));
    }

    /// <summary>
    /// The message a body that walks at no candidate either way has to carry, since it is the one an operator reads
    /// before deciding what to do with the cell: the ids that were not registered, and both knobs by name.
    /// </summary>
    [Fact]
    public void ABodyThatWalksNowhere_NamesTheUnregisteredIdsAndBothKnobs()
    {
        byte[] body = new CellBlobFixtures.BodyBuilder()
            .Entity(1,
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(6, CellBlobFixtures.RandomMovement(new Random(7)))),
                (DroppedExtensionId, CellBlobFixtures.Extension(new byte[6])))
            .ToBody();
        byte[] truncated = body.AsSpan(0, body.Length - 1).ToArray();   // walks at nothing, with or without a registry

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PositionFrameBlobMigration.FrameV2ToV3(truncated, new CellBlobMigrationOptions { Registry = MoveProtocol.CreateRegistry() }));

        output.WriteLine(ex.Message);
        Assert.Contains(DroppedExtensionId.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CellBlobMigrationOptions.Registry), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CellBlobMigrationOptions.AssumedWireGeneration), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One knob, two schema ranges. An assumed generation in the v2 vintage resolves a v2 body outright, and the v3
    /// step must go on INFERRING rather than refusing every v3 body in the same store at a generation that cannot
    /// describe one.
    /// </summary>
    [Fact]
    public void AssumedWireGeneration_InTheV2Range_LeavesTheV3StepInferring()
    {
        const int assumed = 5;
        var options = new CellBlobMigrationOptions
        {
            AssumedWireGeneration = assumed,
            Registry = MoveProtocol.CreateRegistry(),
        };

        byte[] v2 = PlainBody(assumed);
        Assert.Equal(CellBlobRewriter.Rewrite(v2, assumed, Current, widenNetIds: false),
            PositionFrameBlobMigration.FrameV2ToV3(v2, options));

        byte[] v3 = PlainBody(WireGenerationBlobMigration.OldestUnstampedWireGeneration);
        Assert.Equal(
            CellBlobRewriter.Rewrite(v3, WireGenerationBlobMigration.OldestUnstampedWireGeneration, Current, widenNetIds: false),
            WireGenerationBlobMigration.NormalizeV3ToV4(v3, options));
    }

    /// <summary>The same the other way up: an assumed generation in the v3 vintage resolves a v3 body and leaves the
    /// v2 step inferring, so a store holding both vintages does not lose one of them to the knob set for the other.</summary>
    [Fact]
    public void AssumedWireGeneration_InTheV3Range_LeavesTheV2StepInferring()
    {
        int assumed = Current;
        var options = new CellBlobMigrationOptions
        {
            AssumedWireGeneration = assumed,
            Registry = MoveProtocol.CreateRegistry(),
        };

        byte[] v3 = PlainBody(assumed);
        Assert.Equal(CellBlobRewriter.Rewrite(v3, assumed, Current, widenNetIds: false),
            WireGenerationBlobMigration.NormalizeV3ToV4(v3, options));

        byte[] v2 = PlainBody(4);
        Assert.Equal(CellBlobRewriter.Rewrite(v2, 4, Current, widenNetIds: false),
            PositionFrameBlobMigration.FrameV2ToV3(v2, options));
    }

    private static Outcome Migrate(byte[] body, byte[] expected, CellBlobMigrationOptions options)
    {
        byte[] actual;
        try { actual = PositionFrameBlobMigration.FrameV2ToV3(body, options); }
        catch (AmbiguousCellBlobGenerationException) { return Outcome.Ambiguous; }
        catch (InvalidOperationException) { return Outcome.Refused; }
        return actual.AsSpan().SequenceEqual(expected) ? Outcome.Migrated : Outcome.MisDecoded;
    }

    private static void Tally(Dictionary<Outcome, int> counts, Outcome outcome) =>
        counts[outcome] = counts.TryGetValue(outcome, out int n) ? n + 1 : 1;

    private static int Count(Dictionary<Outcome, int> counts, Outcome outcome) =>
        counts.TryGetValue(outcome, out int n) ? n : 0;

    private static string Describe(Dictionary<Outcome, int> counts) =>
        $"{Count(counts, Outcome.Migrated)} migrated, {Count(counts, Outcome.Ambiguous)} ambiguous, " +
        $"{Count(counts, Outcome.Refused)} refused, {Count(counts, Outcome.MisDecoded)} MIS-DECODED";

    // A body written at a known generation whose only extension frame sits at an id no registry here registers: the
    // retain-and-rewrite class, which is exactly the class the registry rule was retiring wholesale.
    private static byte[] RetainedExtensionBody(Random rng, int generation)
    {
        var payload = new byte[6];
        rng.NextBytes(payload);
        return new CellBlobFixtures.BodyBuilder()
            .Entity(rng.Next(1, 5000),
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(generation,
                    new Vector3(rng.Next(-64, 64), rng.Next(0, 32), rng.Next(-64, 64)))),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(generation, CellBlobFixtures.RandomMovement(rng))),
                (DroppedExtensionId, CellBlobFixtures.Extension(payload)))
            .ToBody();
    }

    // A body with nothing adversarial in it, so the assumed-generation tests are about which STEP walks it rather
    // than about the inference being lucky: the fields are tidy values and every frame is a built-in.
    private static byte[] PlainBody(int generation)
    {
        var movement = new MovementState
        {
            VerticalVelocity = -4.5f,
            Grounded = true,
            TimeSinceGrounded = 2.25f,
            JumpBufferRemaining = 0.125f,
            Swimming = false,
            TeleportEpoch = 12u,
            ClimbRateQ = 3,
            SpeedScaleQ = -2,
            HorizontalVelocityXQ = 1000,
            HorizontalVelocityZQ = -1000,
            FacingYawQ = 700,
        };
        return new CellBlobFixtures.BodyBuilder()
            .Entity(41,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(generation, new Vector3(8.5f, 2f, -3.25f))),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(generation, movement)),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Walker")))
            .ToBody();
    }
}
