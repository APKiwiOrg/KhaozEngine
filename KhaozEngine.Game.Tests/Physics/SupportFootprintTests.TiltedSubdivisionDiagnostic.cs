using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    // Quantifies the legacy dual column box, which support no longer reads. The support column is one point
    // inside it, so none of the subdivision measured below is support work.
    [Fact]
    public void TiltedSubdivisionSourceBoxDiagnostic()
    {
        var pose = new RigidPose(new(0, -0.25f, 0),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6));
        Assert.True(InstalledPoseOperator.TryCreate(pose, out var map));
        var sx = map.Y.X.Bounds;
        var sy = map.Y.Y.Bounds;
        _output.WriteLine($"legacy source X=[{sx.Lower:R},{sx.Upper:R}] Y=[{sy.Lower:R},{sy.Upper:R}]");
        Assert.True(InstalledPoseOperator.TryCreateSupport(pose, out var support));
        Vector3 column = InstalledSupportPoseTests.Point(support.Y);
        _output.WriteLine($"support column X={column.X:R} Y={column.Y:R}");
        Assert.InRange((double)column.X, sx.Lower, sx.Upper);
        Assert.InRange((double)column.Y, sy.Lower, sy.Upper);
        Assert.True(sx.Lower < sx.Upper || sy.Lower < sy.Upper);
        var low = Components(-sx.Lower, sy.Lower);
        var high = Components(-sx.Upper, sy.Upper);
        var lowRoot = Stationary(low.U, low.K);
        var highRoot = Stationary(high.U, high.K);
        var lowMaximum = Height(lowRoot, low.U, low.K);
        var highMaximum = Height(highRoot, high.U, high.K);
        _output.WriteLine($"low source u=[{low.U.Lower:R},{low.U.Upper:R}] k=[{low.K.Lower:R},{low.K.Upper:R}]");
        _output.WriteLine($"high source u=[{high.U.Lower:R},{high.U.Upper:R}] k=[{high.K.Lower:R},{high.K.Upper:R}]");
        _output.WriteLine($"low root=[{lowRoot.Lower:R},{lowRoot.Upper:R}] maximum=[{lowMaximum.Lower:R},{lowMaximum.Upper:R}]");
        _output.WriteLine($"high root=[{highRoot.Lower:R},{highRoot.Upper:R}] maximum=[{highMaximum.Lower:R},{highMaximum.Upper:R}]");
        Assert.True(highMaximum.Lower > lowMaximum.Upper);
        double midpoint = highRoot.Lower * 0.5 + highRoot.Upper * 0.5;
        const double width = 0.00001;
        var interval = GeometryInterval.Enclose(midpoint - width, midpoint + width);
        var v = E(1).Subtract(interval.Square()).Sqrt();
        var lateral = E(0.1f).Add(E(0.3f).Multiply(interval));
        var extent = E(0.2f).Square().Subtract(lateral.Square()).Sqrt();
        var t = E(0.3f).Multiply(high.K).Multiply(v).Add(extent).Divide(high.U);
        Assert.True(t.Lower > -1 && t.Upper < 1);
        var second = E(0.3f).Divide(v.Multiply(v).Multiply(v)).Add(high.K.Multiply(E(0.3f).Square())
            .Multiply(E(0.2f).Square()).Divide(extent.Multiply(extent).Multiply(extent))).Divide(high.U);
        var slope = Derivative(E(midpoint), high.U, high.K);
        double slopeAbs = Math.Max(Math.Abs(slope.Lower), Math.Abs(slope.Upper));
        var floor = Height(E(midpoint), high.U, high.K).Subtract(E(slopeAbs).Multiply(E(width)))
            .Subtract(E(0.5).Multiply(second).Multiply(E(width).Square()));
        Assert.True(floor.Lower > lowMaximum.Upper);
        long heightCells = Math.Abs(BitConverter.DoubleToInt64Bits(interval.Upper) -
            BitConverter.DoubleToInt64Bits(interval.Lower));
        long stationaryCells = Math.Abs(BitConverter.DoubleToInt64Bits(lowRoot.Lower) -
            BitConverter.DoubleToInt64Bits(highRoot.Upper));
        _output.WriteLine($"retained interval=[{interval.Lower:R},{interval.Upper:R}] t=[{t.Lower:R},{t.Upper:R}]");
        _output.WriteLine($"uniform upper={lowMaximum.Upper:R} retained height floor={floor.Lower:R}");
        _output.WriteLine($"adjacent binary64 height cells={heightCells} stationary range cells={stationaryCells}");
        Assert.True(heightCells > 1_000_000_000);
        Assert.True(stationaryCells > 1_000_000);
        _output.WriteLine($"layout witness={Unsafe.SizeOf<SupportFootprintWitness>()} member={Unsafe.SizeOf<SupportFootprintMember>()}");
        var memberType = typeof(BepuPhysicsWorld).Assembly.GetType("KhaozEngine.Physics.Bepu.SupportFootprintCollector+Member")!;
        var size = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(memberType).Invoke(null, null);
        _output.WriteLine($"collector member={size} legacy witness={Unsafe.SizeOf<LegacyFootprintWitness>()} " +
            $"legacy member={Unsafe.SizeOf<LegacyFootprintMember>()} legacy collector member={Unsafe.SizeOf<LegacyCollectorMember>()}");
        return;

        static GeometryInterval E(double x) => GeometryInterval.Exact(x);
        static (GeometryInterval U, GeometryInterval K) Components(double x, double y)
        {
            var norm = E(x).Square().Add(E(y).Square()).Sqrt();
            return (E(x).Divide(norm), E(y).Divide(norm));
        }
        static GeometryInterval Height(GeometryInterval z, GeometryInterval u, GeometryInterval k)
        {
            var v = E(1).Subtract(z.Square()).Sqrt();
            var lateral = E(0.1f).Add(E(0.3f).Multiply(z));
            var extent = E(0.2f).Square().Subtract(lateral.Square()).Sqrt();
            return E(-0.25).Add(E(0.3f).Multiply(v).Add(k.Multiply(extent)).Divide(u));
        }
        static GeometryInterval Derivative(GeometryInterval z, GeometryInterval u, GeometryInterval k)
        {
            var v = E(1).Subtract(z.Square()).Sqrt();
            var lateral = E(0.1f).Add(E(0.3f).Multiply(z));
            var extent = E(0.2f).Square().Subtract(lateral.Square()).Sqrt();
            return E(-0.3f).Multiply(z.Divide(v).Add(k.Multiply(lateral).Divide(extent))).Divide(u);
        }
        static GeometryInterval Stationary(GeometryInterval u, GeometryInterval k)
        {
            double a = -0.3, b = -0.05;
            Assert.True(Derivative(E(a), u, k).Lower > 0);
            Assert.True(Derivative(E(b), u, k).Upper < 0);
            while (true)
            {
                double midpoint = a * 0.5 + b * 0.5;
                if (midpoint == a || midpoint == b) break;
                var derivative = Derivative(E(midpoint), u, k);
                if (derivative.Lower > 0) a = midpoint;
                else if (derivative.Upper < 0) b = midpoint;
                else break;
            }
            return GeometryInterval.Enclose(a, b);
        }
    }

    readonly record struct LegacyFootprintWitness(SupportWitnessRole Role, Vector3 Position,
        float PositionErrorMetres, Vector3 Normal, float NormalError);
    readonly record struct LegacyFootprintMember(KhaozEngine.Physics.StaticHandle Static, SupportElementKind Kind, int ElementId,
        Vector3 PlanePoint, float PlanePointErrorMetres, Vector3 Normal, float NormalError, bool HasDiscMaximum,
        double DiscHeightLower, double DiscHeightUpper, byte ProposalMask, byte WitnessCount,
        LegacyFootprintWitness First, LegacyFootprintWitness Second);
    readonly record struct LegacyCollectorMember(SupportPolygon? Polygon, SupportCurvedLeaf Curved, int Part,
        LegacyFootprintMember Value);

}
