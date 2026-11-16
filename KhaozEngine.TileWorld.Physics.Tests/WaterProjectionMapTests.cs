using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;
using B = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Basis;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class WaterProjectionMapTests(ITestOutputHelper output)
{
    // #1755. Face ordinals and approximately equal normals cannot establish a parent projection.
    [Theory]
    [InlineData("bed")]
    [InlineData("surface")]
    [InlineData("wet-and-dry")]
    [InlineData("wet-only")]
    [InlineData("dry-only")]
    [InlineData("split")]
    [InlineData("lower-clipped")]
    [InlineData("unsplit-lower-clipped")]
    public void CapturedFaceMapsExactParentOperandsAndOrderedBasisTuples(string kind)
    {
        var capture = WaterKernelTraceFixture.RealFaceTypes(diagonalNeighbor: kind == "unsplit-lower-clipped", sloped: true);
        var faces = capture.Faces.Where(face => kind switch
        {
            "bed" => face.Plane == 3,
            "surface" => face.Plane == 4,
            "wet-and-dry" => face.Plane < 3 && face.Wet && face.Dry,
            "wet-only" => face.Wet && !face.Dry,
            "dry-only" => face.Dry && !face.Wet,
            "split" => face.Plane < 3 && face.Shape.Planes.Length == 9,
            "unsplit-lower-clipped" => face.Shape.Planes.Length == 8,
            _ => face.Shape.Planes.Length == 10,
        }).ToArray();
        Assert.NotEmpty(faces);
        foreach (var face in faces) Check(capture.Prisms[face.Prism].Shape, face.Shape);
    }

    [Fact]
    public void AppendedApproximatelyEqualNormalNeverAliasesAParentBasis()
    {
        P plane = P.Point(1, 0, 0, 0);
        var parent = new TileWaterConvexShape([plane]);
        P near = P.Point(Math.BitIncrement(1), 0, 0, 0);
        Assert.InRange(near.Normal.X.Low - plane.Normal.X.Low, double.Epsilon, 1e-12);
        var face = new TileWaterConvexShape([plane, near, plane], projectionParent: parent);
        Check(parent, face);
        Assert.Equal(new[] { 0, 1, -1, -1 }, face.ProjectionBasisMap.ToArray());
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("offset")]
    [InlineData("height-sensitive")]
    [InlineData("signed-zero")]
    [InlineData("order")]
    [InlineData("short-prefix")]
    public void UnprovedPlanePrefixHasNoParentRelationship(string difference)
    {
        P first = P.Point(1, 0, 0, 0), second = P.Point(0, 1, 0, 0);
        var parent = new TileWaterConvexShape([first, second]);
        P changed = difference switch
        {
            "normal" => first with { Normal = V.Point(Math.BitIncrement(1), 0, 0) },
            "offset" => first with { Offset = new I(0, Math.BitIncrement(0)) },
            "height-sensitive" => first with { HeightSensitive = true },
            "signed-zero" => first with { Normal = V.Point(1, BitConverter.Int64BitsToDouble(long.MinValue), 0) },
            _ => first,
        };
        P[] planes = difference == "order" ? [second, first] :
            difference == "short-prefix" ? [first] : [changed, second, first.Opposite];
        var unrelated = new TileWaterConvexShape(planes, projectionParent: parent);
        Assert.Null(unrelated.ProjectionParent);
        Assert.True(unrelated.ProjectionBasisMap.IsEmpty);
        var proved = new TileWaterConvexShape([first, second, first.Opposite], projectionParent: parent);
        Check(parent, proved);
    }

    [Fact]
    public void StandaloneCapturesCarryNoMapAndDistinctCapturesKeepTheirOwnParent()
    {
        var standalone = WaterKernelTraceFixture.RepeatedFaces(1, wet: true);
        foreach (var shape in standalone.Prisms.Select(p => p.Shape).Concat(standalone.Faces.Select(f => f.Shape)))
        {
            Assert.Null(shape.ProjectionParent);
            Assert.True(shape.ProjectionBasisMap.IsEmpty);
        }
        var first = WaterKernelTraceFixture.RealFaceTypes();
        var second = WaterKernelTraceFixture.RealFaceTypes();
        Assert.Equal(first.Faces.Length, second.Faces.Length);
        for (int i = 0; i < first.Faces.Length; i++)
        {
            var face = first.Faces[i];
            Assert.Same(first.Prisms[face.Prism].Shape, face.Shape.ProjectionParent);
            Assert.NotSame(face.Shape.ProjectionParent, second.Faces[i].Shape.ProjectionParent);
            var copy = face.Shape.ProjectionBasisMap.ToArray();
            copy[0] = -1;
            Assert.Equal(0, face.Shape.ProjectionBasisMap[0]);
        }
    }

    [Fact]
    public void MapDoesNotChangeEvaluationWithUnavailableControls()
    {
        var capture = WaterKernelTraceFixture.RealFaceTypes();
        V start = V.Point(0.75, -0.5, -0.5), drive = V.Point(0.25, 0.125, -0.125);
        foreach (var face in capture.Faces)
        {
            var piece = capture.Prisms[face.Prism];
            var frozen = new FrozenTileWaterConvexShape(face.Shape);
            var states = new TileWaterPreparedQuery.BasisState[TileWaterPreparedQuery.MaxBases];
            var clips = new TileWaterPreparedQuery.ClipTerm[TileWaterPreparedQuery.MaxBases *
                (TileWaterPreparedQuery.MaxPlanes + 3)];
            var planes = new TileWaterPreparedQuery.PlaneState[TileWaterPreparedQuery.MaxPlanes];
            var prepared = new TileWaterPreparedQuery(face.Shape, states, clips, planes, start, drive, I.Point(0), 3,
                piece.HeightError, piece.HorizontalError);
            var storage = new byte[prepared.StorageBytes];
            prepared.CopyTo(storage);
            var header = MemoryMarshal.Read<TileWaterPreparedQuery.Header>(storage);
            header.HasControlToken = false;
            MemoryMarshal.Write(storage, in header);
            var unavailable = TileWaterPreparedQuery.Restore(face.Shape, storage);
            Assert.False(unavailable.CanRetain);
            FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 3,
                heightError: piece.HeightError, horizontalError: piece.HorizontalError), unavailable.Evaluate());
            Assert.Same(piece.Shape, face.Shape.ProjectionParent);
        }
    }

    [Fact]
    public void DiscardedFaceAndItsParentAreCollectedTogether()
    {
        var references = Release();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.False(references.Face.IsAlive);
        Assert.False(references.Parent.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Face, WeakReference Parent) Release()
    {
        var capture = WaterKernelTraceFixture.RealFaceTypes();
        var face = capture.Faces[0].Shape;
        Assert.Same(capture.Prisms[capture.Faces[0].Prism].Shape, face.ProjectionParent);
        return (new(face), new(face.ProjectionParent!));
    }

    [Fact]
    public void CaptureOwnedMapStorageIsBoundedByItsBasisCount()
    {
        var capture = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        P[] planes = capture.Prisms[0].Planes;
        TileWaterConvexShape? shape = null;
        TileWaterPrismFace[]? faces = null;
        TileWaterTraceTestSnapshot? next = null;
        Action construct = () => shape = new(planes);
        Action build = () => faces = TileWaterPrismFaces.Build(capture.Prisms);
        Action recapture = () => next = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        construct();
        build();
        recapture();
        long shapeBytes = AllocationMeasurement.Measure("standalone water shape", construct);
        long faceBytes = AllocationMeasurement.Measure("water face capture", build);
        long captureBytes = AllocationMeasurement.Measure("complete water capture fixture", recapture, 64 * 1024 * 1024);
        Assert.NotNull(shape);
        Assert.NotNull(faces);
        Assert.NotNull(next);
        output.WriteLine($"MAP_MEMORY shape={shapeBytes} faces={faceBytes} capture={captureBytes} " +
            $"prisms={capture.Prisms.Length} faceCount={faces.Length} " +
            $"bases={string.Join(',', faces.Select(face => face.Shape.Bases.Length))}");
        MeasureArrays(capture, "two-axis-slope");
        MeasureArrays(GrazingWaterBank.Capture().Medium.CaptureTraceForTests(), "bank");
        foreach (var face in faces)
            Assert.Equal(face.Shape.Bases.Length, face.Shape.ProjectionBasisMap.Length);
    }

    void MeasureArrays(TileWaterTraceTestSnapshot capture, string source)
    {
        TileWaterConvexShape? shape = null;
        foreach (int planeCount in new[] { 7, 9 })
        {
            var face = capture.Faces.First(face => face.Shape.Planes.Length == planeCount);
            P[] operands = face.Shape.Planes.ToArray();
            var parent = capture.Prisms[face.Prism].Shape;
            Action plain = () => shape = new(operands);
            Action mapped = () => shape = new(operands, projectionParent: parent);
            plain();
            mapped();
            long plainBytes = AllocationMeasurement.Measure("standalone face geometry", plain);
            long mappedBytes = AllocationMeasurement.Measure("mapped face geometry", mapped);
            output.WriteLine($"MAP_ARRAY source={source} planes={planeCount} bases={face.Shape.Bases.Length} " +
                $"plain={plainBytes} mapped={mappedBytes} added={mappedBytes - plainBytes}");
        }
        GC.KeepAlive(shape);
    }

    static void Check(TileWaterConvexShape parent, TileWaterConvexShape face)
    {
        Assert.Same(parent, face.ProjectionParent);
        Assert.Equal(face.Bases.Length, face.ProjectionBasisMap.Length);
        for (int i = 0; i < parent.Planes.Length; i++)
        {
            P a = parent.Planes[i], b = face.Planes[i];
            Assert.Equal(a.HeightSensitive, b.HeightSensitive);
            double[] left = [a.Normal.X.Low, a.Normal.X.High, a.Normal.Y.Low, a.Normal.Y.High,
                a.Normal.Z.Low, a.Normal.Z.High, a.Offset.Low, a.Offset.High];
            double[] right = [b.Normal.X.Low, b.Normal.X.High, b.Normal.Y.Low, b.Normal.Y.High,
                b.Normal.Z.Low, b.Normal.Z.High, b.Offset.Low, b.Offset.High];
            Assert.Equal(left.Select(BitConverter.DoubleToInt64Bits), right.Select(BitConverter.DoubleToInt64Bits));
        }
        B[] parentBases = parent.Bases.ToArray();
        for (int i = 0; i < face.Bases.Length; i++)
        {
            B basis = face.Bases[i];
            bool appended = basis.A >= parent.Planes.Length || basis.B >= parent.Planes.Length ||
                basis.C >= parent.Planes.Length;
            int expected = appended ? -1 : Array.FindIndex(parentBases, candidate => candidate == basis);
            Assert.Equal(expected, face.ProjectionBasisMap[i]);
        }
        Assert.Equal(new B(-1), face.Bases[0]);
        Assert.Equal(0, face.ProjectionBasisMap[0]);
        Assert.Contains(-1, face.ProjectionBasisMap.ToArray());
        Assert.Contains(face.ProjectionBasisMap.ToArray(), ordinal => ordinal > 0);
    }
}
