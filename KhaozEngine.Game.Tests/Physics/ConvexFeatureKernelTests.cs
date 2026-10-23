using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexFeatureKernelTests
{
    readonly ITestOutputHelper _output;

    public ConvexFeatureKernelTests(ITestOutputHelper output) => _output = output;

    void Record(in ConvexQueryWork work, ConvexQueryScratch scratch) => _output.WriteLine(
        $"Root high-water cells={work.CellHighWater} features={work.FeatureHighWater} coefficients={scratch.Polynomials.HighWater}");

    [Fact]
    public void ClosedDoubleRootIsContact()
    {
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var regime = new ConvexFeatureRegime(ConvexPolynomial.From(0.25, -1, 1));
        Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var first));
        Assert.True(first.HasRoot);
        Assert.Equal(0.5, first.Lower);
        Assert.Equal(0.5, first.Upper);
        Record(work, scratch);
    }

    [Fact]
    public void NonDyadicRepeatedRootIsEnclosedWithoutASignCrossing()
    {
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        // (3t - 1)^2 (2t + 1). The only root in [0,1] is the double root 1/3.
        var regime = new ConvexFeatureRegime(ConvexPolynomial.From(1, -4, -3, 18));
        Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var first));
        Assert.True(first.HasRoot);
        Assert.True(first.Lower * 3 <= 1 && first.Upper * 3 >= 1);
        Assert.InRange(first.Upper - first.Lower, 0, scratch.MaximumFractionWidth);
        Record(work, scratch);
    }

    [Fact]
    public void SquaredRadicalRootRetainsOriginalSignAndClosedDomain()
    {
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        // Squaring t - 1/2 = -sqrt(1/16) introduces t=3/4. Only t=1/4 is genuine.
        var equation = ConvexPolynomial.From(0.1875, -1, 1);
        var sign = new ConvexRadicalPredicate(ConvexPolynomial.From(0.5, -1),
            ConvexPolynomial.From(-1), ConvexPolynomial.From(0.0625));
        var regime = new ConvexFeatureRegime(equation, [ConvexPolynomial.From(1, -1)], [sign]);
        Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var first));
        Assert.True(first.HasRoot);
        Assert.Equal(0.25, first.Lower);
        Assert.Equal(0.25, first.Upper);
        Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0.5, 1, ref work, scratch, out var empty));
        Assert.False(empty.HasRoot);
        Assert.True(empty.IsComplete);
        Record(work, scratch);
    }

    [Fact]
    public void ClosedFeatureBoundaryDoesNotBecomeAnEmptyInterval()
    {
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var regime = new ConvexFeatureRegime(ConvexPolynomial.From(0),
            [ConvexPolynomial.From(-1, 3), ConvexPolynomial.From(1, -3)]);
        Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var first));
        Assert.True(first.HasRoot);
        Assert.True(first.Lower * 3 <= 1 && first.Upper * 3 >= 1);
        Record(work, scratch);
    }

    [Fact]
    public void ExhaustionAndExactOverflowRefuseWithoutLeakingAnEarlierRoot()
    {
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork(maximumCells: 1);
        Assert.True(work.Primitive());
        var regime = new ConvexFeatureRegime(ConvexPolynomial.From(-2, 0, 4));
        Assert.False(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var refused));
        Assert.Equal(default, refused);
        Assert.True(work.Exhausted);
        Assert.False(work.Primitive());
        Assert.False(work.Feature());
        Assert.False(work.Cell());

        var large = ConvexPolynomial.From(double.MaxValue, double.Epsilon, 0, 1);
        work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        Assert.False(ConvexAlgebraicRoots.TryIsolate(new(large), 0, 1, ref work, scratch, out refused));
        Assert.Equal(default, refused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZeroCellBudgetCannotPublishAnInitialRoot(bool identicallyZero)
    {
        var work = new ConvexQueryWork(maximumCells: 0);
        Assert.True(work.Primitive());
        var polynomial = identicallyZero ? ConvexPolynomial.From(0) : ConvexPolynomial.From(0, 1);
        Assert.False(ConvexAlgebraicRoots.TryIsolate(new(polynomial), 0, 1, ref work,
            new ConvexQueryScratch(), out var evidence));
        Assert.Equal(default, evidence);
        Assert.True(work.Exhausted);
    }

    [Fact]
    public void QuarticCanRejectAnEarlierRootAndRetainTheNextFeasibleRoot()
    {
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var regime = new ConvexFeatureRegime(ConvexPolynomial.From(0.125, 0, -0.75, 0, 1),
            [ConvexPolynomial.From(-0.625, 1)]);
        Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var root));
        Assert.True(root.HasRoot);
        Assert.True(root.Lower * root.Lower <= 0.5 && root.Upper * root.Upper >= 0.5);
        Assert.True(root.Lower > 0.625);
        Record(work, scratch);
    }

    [Fact]
    public void DyadicCircleTangencyAndBothAdjacentFloatsStayDistinct()
    {
        foreach (float height in new[] { MathF.BitDecrement(0.25f), 0.25f, MathF.BitIncrement(0.25f) })
        {
            var scratch = new ConvexQueryScratch();
            var work = new ConvexQueryWork();
            Assert.True(work.Primitive());
            // The represented binary32 square and its dyadic subtraction are exact in binary64.
            var regime = new ConvexFeatureRegime(ConvexPolynomial.From(0.25 + (double)height * height - 0.0625, -1, 1));
            Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var root));
            Assert.Equal(height <= 0.25f, root.HasRoot);
            if (height == 0.25f) Assert.Equal(new ConvexRootBracket(true, true, 0.5, 0.5), root);
            if (height < 0.25f) Assert.True(root.Lower < 0.5);
            Record(work, scratch);
        }
    }

    [Fact]
    public void ExactPolynomialStorageCannotFallBackBeyondTheScratchArena()
    {
        var scratch = new ConvexQueryScratch(maximumCoefficients: 4);
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var regime = new ConvexFeatureRegime(ConvexPolynomial.From(0.25, -1, 1));
        Assert.False(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var root));
        Assert.Equal(default, root);
        Assert.True(work.Exhausted);
        Assert.Equal(0, scratch.Polynomials.Used);

        work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        Assert.True(ConvexAlgebraicRoots.TryIsolate(new(ConvexPolynomial.From(0)), 0, 1,
            ref work, scratch, out var initial));
        Assert.Equal(new ConvexRootBracket(true, true, 0, 0), initial);
        Assert.Equal(0, scratch.Polynomials.Used);
    }

    [Fact]
    public void IdenticallySquaredRadicalStillRetainsItsUnsquaredCoefficientBoundary()
    {
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        // (t-1/2) + (t-1/2)sqrt(1) >= 0 starts at 1/2, although K*K-L*L*D is identically zero.
        var radical = new ConvexRadicalPredicate(ConvexPolynomial.From(-0.5, 1),
            ConvexPolynomial.From(-0.5, 1), ConvexPolynomial.From(1));
        var regime = new ConvexFeatureRegime(ConvexPolynomial.From(0), RadicalDomains: [radical]);
        Assert.True(ConvexAlgebraicRoots.TryIsolate(regime, 0, 1, ref work, scratch, out var root));
        Assert.True(root.HasRoot);
        Assert.Equal(new ConvexRootBracket(true, true, 0.5, 0.5), root);
        Record(work, scratch);
    }

    [Fact]
    public void CircleSupportIsNeverReplacedByInscribedVertices()
    {
        foreach (var shape in ConvexQueryFixture.Bodies())
        {
            // An off-axis direction between any axis-aligned polygon vertices.
            Vector3 direction = new(3, 0, 4);
            Vector3 proposal = ConvexQueryOperations.Support(shape, direction);
            var oracle = ShortBodyShellOracle.SupportBounds(shape, direction);
            // Account only for the actual binary32 output encoding, not an acceptance epsilon.
            double lower = 3d * MathF.BitDecrement(proposal.X) + 4d * MathF.BitDecrement(proposal.Z);
            double upper = 3d * MathF.BitIncrement(proposal.X) + 4d * MathF.BitIncrement(proposal.Z);
            Assert.True(lower <= oracle.Lower && upper >= oracle.Upper);
            Assert.Equal(shape.RingY, proposal.Y);
        }
    }
}
