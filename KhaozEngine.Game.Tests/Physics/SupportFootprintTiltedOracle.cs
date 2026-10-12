using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;

namespace KhaozEngine.Tests.Physics;

/// <summary>Exact finite-end crest over the independent installed rational source.
/// A numerical optimizer's flat binary64 objective cannot certify its selected parameter.</summary>
internal static class SupportFootprintTiltedOracle
{
    internal static (P Position, P Normal) Crest(SupportFootprintCurvedProofOracle.InstalledPart part, bool cap)
    {
        P axis = part.Axis;
        B square = axis.X * axis.X + axis.Z * axis.Z;
        B u = new(B.Root(square.Lower).Lower, B.Root(square.Upper).Upper);
        var w = new P(-axis.Y * axis.X / u, u, -axis.Y * axis.Z / u);
        B h = part.Half, r = part.Radius;
        return (new(part.Centre.X + axis.X * h + w.X * r,
            part.Centre.Y + axis.Y * h + w.Y * r, part.Centre.Z + axis.Z * h + w.Z * r), cap ? axis : w);
    }
}
