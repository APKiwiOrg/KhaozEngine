using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Supplied represented points and triangles only. No projection, transform or contact is certified.
public class CapsuleFeaturePredicateTests
{
    [Theory]
    [MemberData(nameof(RepresentedTriangleCases))]
    public void ClosedRepresentedTriangleHasItsIndependentNamedClassification(
        string caseName, Vector3 a, Vector3 b, Vector3 c, Vector3 point, string expectedName)
    {
        Assert.True(typeof(CapsuleFeaturePredicates).IsNotPublic, caseName);
        Assert.True(typeof(TrianglePointLocation).IsNotPublic, caseName);
        Assert.Equal(TrianglePointLocation.Unresolved, default);
        Assert.Equal(expectedName, CapsuleFeaturePredicates.ClassifyTrianglePoint(a, b, c, point).ToString());
    }

    public static IEnumerable<object[]> RepresentedTriangleCases()
    {
        Vector3 a = Vector3.Zero, b = new(4, 0, 0), c = new(0, 4, 0);
        float negativeZero = BitConverter.Int32BitsToSingle(int.MinValue);

        // The XY triangle is z=0, x>=0, y>=0, x+y<=4. Its barycentrics are (1-x/4-y/4, x/4, y/4).
        yield return ["HalfQuarterQuarterBarycentricsAreInterior", a, b, c,
            new Vector3(1, 1, 0), "Interior"];
        yield return ["ABMidpointHasOneZeroBarycentric", a, b, c,
            new Vector3(2, 0, 0), "Edge"];
        yield return ["BCMidpointIsOnTheClosedHypotenuse", a, b, c,
            new Vector3(2, 2, 0), "Edge"];
        yield return ["SignedZeroOriginHasTwoZeroBarycentrics", a, b, c,
            new Vector3(negativeZero, 0, negativeZero), "Vertex"];
        yield return ["BEndpointHasTwoZeroBarycentrics", a, b, c,
            b, "Vertex"];
        yield return ["SumFiveExceedsTheClosedHypotenuse", a, b, c,
            new Vector3(3, 2, 0), "Outside"];
        yield return ["NegativeTwoToMinus149IsStrictlyOutsideAB", a, b, c,
            new Vector3(2, -float.Epsilon, 0), "Outside"];
        yield return ["PositiveTwoToMinus149IsExactlyOffPlane", a, b, c,
            new Vector3(1, 1, float.Epsilon), "Outside"];
        yield return ["ReversedWindingPreservesPositiveBarycentrics", a, c, b,
            new Vector3(1, 1, 0), "Interior"];
        yield return ["CyclicCoordinatePermutationPreservesInterior", a,
            new Vector3(0, 4, 0), new Vector3(0, 0, 4), new Vector3(0, 1, 1), "Interior"];
        yield return ["ExactIntegerTranslationPreservesInterior", new Vector3(64, -32, 16),
            new Vector3(68, -32, 16), new Vector3(64, -28, 16), new Vector3(65, -31, 16), "Interior"];

        // z=x+y is an oblique plane. (1,1,2) has the same (1/2,1/4,1/4) barycentrics.
        yield return ["ObliquePlaneHasHalfQuarterQuarterBarycentrics", a,
            new Vector3(4, 0, 4), new Vector3(0, 4, 4), new Vector3(1, 1, 2), "Interior"];
        yield return ["ObliquePlaneResidualTwoToMinus22IsNonzero", a,
            new Vector3(4, 0, 4), new Vector3(0, 4, 4),
            new Vector3(1, 1, 2.0000002384185791015625f), "Outside"];

        // d=2^-23. B=(1,1+d,0), C=(1-d,1,0), so the exact cross Z is d^2=2^-46.
        // Binary32 rounds (1+d)*(1-d) to 1. No float cross product constructs these expectations.
        Vector3 skinnyB = new(1, 1.00000011920928955078125f, 0);
        Vector3 skinnyC = new(0.99999988079071044921875f, 1, 0);
        yield return ["CancellationDeterminantTwoToMinus46StillHasABMidpoint", a, skinnyB, skinnyC,
            new Vector3(0.5f, 0.500000059604644775390625f, 0), "Edge"];
        yield return ["CancellationTriangleRequiresOneNegativeTwoTo22Barycentric", a, skinnyB, skinnyC,
            new Vector3(0.5f, 0.5f, 0), "Outside"];

        // ScaleB selects normal represented powers of two exactly. Cross Z=2^-160 underflows binary32.
        float tinySide = MathF.ScaleB(1f, -80), tinyQuarter = MathF.ScaleB(1f, -82);
        yield return ["UnderflowDeterminantTwoToMinus160HasPositiveQuarterBarycentrics", a,
            new Vector3(tinySide, 0, 0), new Vector3(0, tinySide, 0),
            new Vector3(tinyQuarter, tinyQuarter, 0), "Interior"];

        // Degeneracy precedes membership, including a coincident vertex or an off-line supplied point.
        yield return ["RepeatedVertexHasZeroAreaEvenAtThatVertex", a, a, b, a, "Degenerate"];
        yield return ["CollinearThreeDimensionalTriangleHasZeroArea", a,
            new Vector3(2, 2, 2), new Vector3(4, 4, 4), new Vector3(1, 1, 2), "Degenerate"];
        yield return ["NaNVertexCannotEstablishAnyTriangleRelation", new Vector3(float.NaN, 0, 0),
            b, c, new Vector3(1, 1, 0), "Unresolved"];
        yield return ["InfinitePointCannotEstablishOutsideOrMembership", a, b, c,
            new Vector3(1, 1, float.PositiveInfinity), "Unresolved"];
    }
}
