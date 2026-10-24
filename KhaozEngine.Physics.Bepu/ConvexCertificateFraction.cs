using System;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using Root = KhaozEngine.Physics.Bepu.ConvexAlgebraicRoots.Retained;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A quadratic time or affine barycentric expression in one retained circular root.
/// There is no algebraic composition door. Independent roots refuse comparison.</summary>
internal readonly struct ConvexCertificateFraction
{
    readonly Q _value;
    internal Root? Root { get; }
    internal ConvexCircularCertificate? Circular { get; }
    internal ConvexRational Offset { get; }
    internal ConvexRational Scale { get; }
    internal Q Value => Root is null ? _value : throw new ConvexExactRefusalException();
    internal GeometryInterval Bounds => Root is null ? _value.Bounds : new Q(Offset).Bounds.Add(new Q(Scale).Bounds.Multiply(Root.Bounds));
    internal bool IsValid => Root is null ? _valid : _valid && Root.IsAlive;
    readonly bool _valid;
    ConvexCertificateFraction(Q value)
    { _value = value; _valid = value.Sign >= 0 && (Q.From(1) - value).Sign >= 0; }
    ConvexCertificateFraction(Root root, ConvexRational offset, ConvexRational scale, ConvexCircularCertificate? circular)
    { Root = root; Offset = offset; Scale = scale; Circular = circular; _valid = true; }
    internal static ConvexCertificateFraction From(double value) => new(Q.From(value));
    internal static ConvexCertificateFraction From(Q value) => new(value);
    internal static ConvexCertificateFraction FromRoot(Root root, double offset, double scale) =>
        new(root, ConvexRational.From(offset), ConvexRational.From(scale), null);
    internal static ConvexCertificateFraction FromCircular(ConvexCircularCertificate circular, double offset, double scale) =>
        new(circular.Root, ConvexRational.From(offset), ConvexRational.From(scale), circular);
    internal static ConvexCertificateFraction FromCircular(ConvexCircularCertificate circular, ConvexRational offset, ConvexRational scale) =>
        new(circular.Root, offset, scale, circular);

    internal bool TryCompare(ConvexCertificateFraction other, ref ConvexQueryWork work, ConvexQueryScratch scratch, out int sign)
    {
        sign = 0;
        if (!IsValid || !other.IsValid || work.Exhausted || !work.Feature()) return false;
        int checkpoint = scratch.Polynomials.Used;
        try
        {
            if (Root is null && other.Root is null) { sign = (_value - other._value).Sign; return true; }
            if (Root is null)
            {
                if (!other.TryCompare(this, ref work, scratch, out sign)) return false;
                sign = -sign; return true;
            }
            if (other.Root is not null && !ReferenceEquals(Root, other.Root)) return false;
            ConvexRational offset = Offset - (other.Root is null ? other._value.A : other.Offset);
            ConvexRational scale = Scale - (other.Root is null ? default : other.Scale);
            var k = scale.IsZero ? scratch.Polynomials.Constant(offset) : scratch.Polynomials.Create(2);
            if (!scale.IsZero) { k.Set(0, offset); k.Set(1, scale); }
            if (other.Root is not null || other._value.Rational) return Root.TrySign(k, ref work, scratch, out sign);
            return Root.TryRadicalSign(new(k, scratch.Polynomials.Constant(-other._value.B), scratch.Polynomials.Constant(other._value.D)), ref work, scratch, out sign);
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally { scratch.Polynomials.Restore(checkpoint); }
    }

    internal bool TryRefine(double width, ref ConvexQueryWork work, ConvexQueryScratch scratch, out GeometryInterval bounds)
    {
        bounds = default;
        if (!IsValid || !double.IsFinite(width) || width <= 0 || work.Exhausted || !work.Feature()) return false;
        try
        {
            if (Root is null) { bounds = _value.Bounds; return bounds.IsResolved && bounds.Upper - bounds.Lower <= width; }
            if (!ReferenceEquals(Root.Owner, scratch)) return false;
            double scale = Math.Max(Math.Abs(new Q(Scale).Bounds.Lower), Math.Abs(new Q(Scale).Bounds.Upper));
            if (scale > 0 && !Root.TryRefine(width / (2 * scale), ref work, scratch)) return false;
            bounds = new Q(Offset).Bounds.Add(new Q(Scale).Bounds.Multiply(Root.Bounds));
            return bounds.IsResolved && bounds.Upper - bounds.Lower <= width;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
    }
}
