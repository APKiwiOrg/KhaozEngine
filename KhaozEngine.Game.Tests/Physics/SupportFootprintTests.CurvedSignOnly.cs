using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void CurvedCollectionHasItsOwnPhase()
    {
        SupportQueryDiagnostics.Enable(true);
        var recorder = new SupportCostAttributionTests.PhaseRecorder(false);
        ContactAllocationDiagnostics.SetObserver(recorder.Observe);
        SupportCostSample sample;
        try
        {
            sample = new SupportCostSampler(false) { BeforeWarm = recorder.Reset, IsolateAllocations = true }
                .Measure(CostRows().Single(row => row.Name == "sphere"), "Find")[0];
        }
        finally
        {
            ContactAllocationDiagnostics.SetObserver(null);
            SupportQueryDiagnostics.Enable(false);
        }
        Assert.True(sample.GeometryMatchesOracle);
        Assert.Equal(0, recorder.Depth);
        Assert.Equal(0, sample.WarmBytes);
        Assert.Equal(sample.WarmBytes, recorder.Bytes.Sum());
        // The sphere's contact kernel runs once per contact proposal, nested inside its curved collection.
        Assert.Contains(recorder.Rows(), row => row.Phase == "SupportCurved" && row.Calls > 0);
        Assert.Contains(recorder.Rows(), row => row.Phase == "SupportCurvedContact" && row.Calls > 0);
    }

    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedRowsMatchReference(string name)
    {
        using var counters = new WorldFormCounters();
        SupportCostRow row = CostRows().Single(r => r.Name == name);
        using (var raw = new WorldFormScene(row))
        {
            raw.CheckedFootprint($"{name} cold raw members");
            raw.CheckedFootprint($"{name} warm raw members");
        }
        using var scene = new WorldFormScene(row);
        scene.Checked($"{name} cold", out _);
        scene.Checked($"{name} warm", out long warmBuilds);
        Assert.Equal(0, warmBuilds);
        scene.CheckedSubstep($"{name} substep");
    }

    // The exact warm count pins where the certificate fires on each row.
    [Theory]
    [InlineData("sphere", 6)]
    [InlineData("capsule", 6)]
    [InlineData("cylinder", 6)]
    public void CurvedSignOnlyStopsEarly(string name, int warmSamples)
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(r => r.Name == name));
        SupportQueryDiagnostics.Reset();
        SupportSample cold = scene.Find();
        SupportQueryCounts coldCounts = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        SupportSample warm = scene.Find();
        SupportQueryCounts warmCounts = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        SupportSample reference;
        using (SupportReferenceEvaluation.Begin()) reference = scene.Find();
        SupportQueryCounts referenceCounts = SupportQueryDiagnostics.Snapshot();
        _output.WriteLine($"{name}: {warmCounts.TangentSamples} tangent samples against the reference's " +
            $"{referenceCounts.TangentSamples}.");

        Assert.Equal(reference.ToString(), warm.ToString());
        Assert.Equal(cold.ToString(), warm.ToString());
        Assert.Equal(coldCounts with { WorldFormBuilds = 0 }, warmCounts);
        Assert.Equal(warmSamples, warmCounts.TangentSamples);
        Assert.True(warmCounts.TangentSamples < referenceCounts.TangentSamples,
            $"{name}: {warmCounts.TangentSamples} tangent samples against the reference's {referenceCounts.TangentSamples}.");
    }

    /// <summary>Sweeps upright probe axes along and across each curved shape, upright and tilted, part by part, and
    /// compares the sign-only verdict with the full kernel's at every sample. Between neighbouring grid samples whose
    /// full verdict or early stop differs, binary32 bisection finds the change and every represented value within
    /// a few ulps of it is compared too, so samples land on the contact band, the tangent boundary and the narrow
    /// window where an early stop begins or ends.</summary>
    [Fact]
    public void CurvedSignOnlyMatchesFullKernelAcrossTheBand()
    {
        using var counters = new WorldFormCounters();
        var sweep = new TangentSweep(new SupportTangentKernel(), SupportCertification.ContactBand);
        Quaternion tilted = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 0, 0.4f)), 0.5f);
        foreach ((SupportCurvedShape shape, float half) in new[] { (SupportCurvedShape.Sphere, 0f),
            (SupportCurvedShape.Capsule, 0.5f), (SupportCurvedShape.Cylinder, 0.5f) })
            foreach (Quaternion orientation in new[] { Quaternion.Identity, tilted })
            {
                var leaves = new List<SupportCurvedLeaf>();
                Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodCurved.Leaf(shape, 0.5f, half,
                    new RigidPose(Vector3.Zero, orientation), 0, leaves));
                for (int part = 0; part < leaves[0].Parts; part++)
                    foreach (float probe in new[] { FootSupport.AxisProbeRadius, 0.4f })
                    {
                        sweep.Aim(leaves[0], part, probe, $"{shape} {orientation} part {part} probe {probe}");
                        float top = 0.5f + half;
                        foreach ((float x, float z) in new[] { (0f, 0f), (0.2f, 0.03f), (0.45f, 0f), (0.5f, 0f),
                            (0.5f + probe, 0f), (0.7f, 0.1f) })
                            sweep.Along(y => new(x, y, z), -1.3f, top + probe + 0.1f);
                        foreach (float y in new[] { top + probe + FootSupport.ProbeLength / 2, top, 0.3f, 0 })
                            sweep.Along(x => new(x, y, 0.02f), 0, 1.2f + probe);
                    }
            }
        _output.WriteLine(sweep.ToString());
        Assert.True(sweep.Positive > 0 && sweep.Negative > 0, sweep.ToString());
        Assert.True(sweep.EarlyPositive > 0, sweep.ToString());
        Assert.True(sweep.EarlyNegative > 0, sweep.ToString());
        Assert.True(sweep.Windows > 0, sweep.ToString());
        Assert.True(sweep.SignSamples < sweep.FullSamples, sweep.ToString());
    }

    /// <summary>An unresolved probe radius leaves the full kernel's separation unresolved. The sign-only kernel must
    /// return that refusal even where every kept span is certainly behind the cylinder side.</summary>
    [Fact]
    public void CurvedSignOnlyKeepsTheUnresolvedRadiusRefusal()
    {
        using var counters = new WorldFormCounters();
        var leaves = new List<SupportCurvedLeaf>();
        Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodCurved.Leaf(SupportCurvedShape.Cylinder, 0.5f,
            0.5f, new RigidPose(Vector3.Zero, Quaternion.Identity), 0, leaves));
        var kernel = new SupportTangentKernel();
        var start = FeaturePoint.Exact(new(0.25f, 0.55f, 0));
        var end = FeaturePoint.Exact(new(0.25f, 0.56f, 0));
        kernel.Reset(start, end, default, SupportCertification.ContactBand);
        Assert.Equal(GeometrySign.Unresolved, kernel.Measure(default, leaves[0], SupportNeighborhoodCurved.Side, out _));
        Assert.Equal(GeometrySign.Unresolved, kernel.MeasureSign(default, leaves[0], SupportNeighborhoodCurved.Side));
        // A probe this wide reaches the side from inside its rim, so only the front check decides Negative.
        kernel.Reset(start, end, GeometryInterval.Exact(0.3), SupportCertification.ContactBand);
        Assert.Equal(GeometrySign.Negative, kernel.Measure(default, leaves[0], SupportNeighborhoodCurved.Side, out _));
        SupportQueryDiagnostics.Reset();
        Assert.Equal(GeometrySign.Negative, kernel.MeasureSign(default, leaves[0], SupportNeighborhoodCurved.Side));
        long early = SupportQueryDiagnostics.Snapshot().TangentSamples;
        SupportQueryDiagnostics.Reset();
        kernel.Measure(default, leaves[0], SupportNeighborhoodCurved.Side, out _);
        Assert.True(early < SupportQueryDiagnostics.Snapshot().TangentSamples);
    }

    /// <summary>Outside the certificate's domain no early stop fires. A probe this wide would certify at the root
    /// span, so the sign-only kernel must sample exactly what the full kernel does.</summary>
    [Fact]
    public void CurvedSignOnlyRunsTheFullKernelOutsideItsDomain()
    {
        using var counters = new WorldFormCounters();
        var leaves = new List<SupportCurvedLeaf>();
        Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodCurved.Leaf(SupportCurvedShape.Sphere, 0.5f,
            0, new RigidPose(Vector3.Zero, Quaternion.Identity), 0, leaves));
        var kernel = new SupportTangentKernel();
        var start = FeaturePoint.Exact(new(0, 0.6f, 0));
        var end = FeaturePoint.Exact(new(0, 0.61f, 0));
        foreach (double radius in new[] { 0.1, 1 << 21 })
        {
            kernel.Reset(start, end, GeometryInterval.Exact(radius), SupportCertification.ContactBand);
            SupportQueryDiagnostics.Reset();
            GeometrySign full = kernel.Measure(default, leaves[0], 0, out _);
            long fullSamples = SupportQueryDiagnostics.Snapshot().TangentSamples;
            SupportQueryDiagnostics.Reset();
            Assert.Equal(full, kernel.MeasureSign(default, leaves[0], 0));
            long signSamples = SupportQueryDiagnostics.Snapshot().TangentSamples;
            if (radius < 1) Assert.True(signSamples < fullSamples, $"{signSamples} against {fullSamples} samples.");
            else Assert.Equal(fullSamples, signSamples);
        }
    }

    /// <summary>One curved leaf part and probe radius, compared along lines of represented axis positions.</summary>
    sealed class TangentSweep(SupportTangentKernel kernel, double band)
    {
        const int Grid = 32, Window = 12;
        SupportCurvedLeaf _leaf;
        int _part;
        float _probe;
        string _context = "";

        internal int Positive, Negative, Unresolved, EarlyPositive, EarlyNegative, Windows;
        internal long SignSamples, FullSamples;

        internal void Aim(SupportCurvedLeaf leaf, int part, float probe, string context) =>
            (_leaf, _part, _probe, _context) = (leaf, part, probe, context);

        internal void Along(Func<float, Vector3> line, float from, float to)
        {
            float previous = from;
            (GeometrySign, bool) key = Compare(line(from));
            for (int i = 1; i <= Grid; i++)
            {
                float next = i == Grid ? to : from + (to - from) * i / Grid;
                (GeometrySign, bool) nextKey = Compare(line(next));
                if (nextKey != key) Refine(line, previous, next, key);
                (previous, key) = (next, nextKey);
            }
        }

        // Bisects binary32 values to the change, then compares every value within Window ulps on each side of it.
        void Refine(Func<float, Vector3> line, float low, float high, (GeometrySign, bool) lowKey)
        {
            while (true)
            {
                float middle = low * 0.5f + high * 0.5f;
                if (!(middle > low && middle < high)) break;
                if (Compare(line(middle)) == lowKey) low = middle;
                else high = middle;
            }
            Windows++;
            float value = low;
            for (int i = 0; i < Window; i++) value = MathF.BitDecrement(value);
            for (int i = 0; i < 2 * Window + 2; i++, value = MathF.BitIncrement(value)) Compare(line(value));
            // The reference evaluator runs the full kernel unchanged.
            Aimed(line(low));
            GeometrySign full = Full(out long fullSamples), reference;
            long referenceSamples;
            using (SupportReferenceEvaluation.Begin()) reference = Sign(out referenceSamples);
            Assert.Equal(full, reference);
            Assert.Equal(fullSamples, referenceSamples);
        }

        // The full verdict, and whether the sign-only kernel stopped before full resolution.
        (GeometrySign, bool) Compare(Vector3 bottom)
        {
            Aimed(bottom);
            GeometrySign full = Full(out long fullSamples);
            GeometrySign sign = Sign(out long signSamples);
            Assert.True(full == sign, $"{_context} at {bottom:R}: sign-only {sign}, full {full}.");
            Assert.True(signSamples <= fullSamples, $"{_context} at {bottom:R}: {signSamples} > {fullSamples} samples.");
            bool early = signSamples < fullSamples;
            SignSamples += signSamples;
            FullSamples += fullSamples;
            if (full == GeometrySign.Positive) { Positive++; EarlyPositive += early ? 1 : 0; }
            else if (full == GeometrySign.Negative) { Negative++; EarlyNegative += early ? 1 : 0; }
            else Unresolved++;
            return (full, early);
        }

        // The probe's contact segment as the collector retains it, from the core centre half a probe length either way.
        void Aimed(Vector3 bottom)
        {
            var centre = FeaturePoint.Exact(bottom + new Vector3(0, FootSupport.ProbeLength / 2, 0));
            FeatureNumber half = FeatureNumber.Exact(FootSupport.ProbeLength).Multiply(FeatureNumber.Exact(0.5));
            kernel.Reset(new(centre.X, centre.Y.Subtract(half), centre.Z), new(centre.X, centre.Y.Add(half), centre.Z),
                GeometryInterval.Exact(_probe), band);
        }

        GeometrySign Full(out long samples)
        {
            SupportQueryDiagnostics.Reset();
            GeometrySign verdict = kernel.Measure(default, _leaf, _part, out _);
            samples = SupportQueryDiagnostics.Snapshot().TangentSamples;
            return verdict;
        }

        GeometrySign Sign(out long samples)
        {
            SupportQueryDiagnostics.Reset();
            GeometrySign verdict = kernel.MeasureSign(default, _leaf, _part);
            samples = SupportQueryDiagnostics.Snapshot().TangentSamples;
            return verdict;
        }

        public override string ToString() => $"positive {Positive} (early {EarlyPositive}), negative {Negative} " +
            $"(early {EarlyNegative}), unresolved {Unresolved}, windows {Windows}, samples {SignSamples} sign-only " +
            $"against {FullSamples} full.";
    }
}
