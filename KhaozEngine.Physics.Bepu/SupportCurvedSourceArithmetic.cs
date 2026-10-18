using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Closed-form expressions over one unnormalized represented source column, installed as exact constants.
/// Radical signs are reduced under sign guards to the existing degree-eight polynomial owner, whose constant
/// polynomials decide the remaining signs exactly. Storage is reused, including fallback scratch.</summary>
internal sealed class SupportCurvedSourceArithmetic
{
    // Handles stay valid until Reset. Enclosures live in the graph rather than recursive stack frames.
    internal readonly record struct Number(int Id);
    enum Operation : byte { Constant, Add, Multiply, Divide, Root }
    readonly record struct Node(Operation Op, Number Left, Number Right, int Root, double Constant, FeatureNumber Value);
    readonly record struct Fraction(Number A, Number B, Number D);
    readonly record struct Polynomial(SupportSourcePolynomial N, SupportSourcePolynomial D);
    const int MaximumNodes = 4096;
    readonly Node[] _nodes = new Node[MaximumNodes];
    readonly SupportSourcePolynomialWorkspace _work = new();
    readonly Polynomial?[] _compiled = new Polynomial?[MaximumNodes];
    int _count, _compiledCount;
    SupportSourcePolynomial _onePolynomial = null!;
    bool _resolved;
    internal Number Zero { get; private set; }
    internal Number One { get; private set; }
    internal Number X { get; private set; }
    internal Number Y { get; private set; }
    internal Number Z { get; private set; }

    internal int Checkpoint => _count;
    internal void Restore(int checkpoint)
    {
        _count = checkpoint;
        _resolved = true;
    }

    internal void Reset(Vector3 source)
    {
        _count = 0; _resolved = true;
        Zero = Constant(0); One = Constant(1);
        X = Constant(source.X); Y = Constant(source.Y); Z = Constant(source.Z);
    }

    FeatureNumber Value(Number number) => _resolved ? _nodes[number.Id].Value : default;
    internal GeometryInterval Bounds(Number number) => Value(number).Bounds;

    internal Number Constant(double value) => New(Operation.Constant, default, default, FeatureNumber.Exact(value), value);
    internal Number Add(Number a, Number b)
    {
        if (a.Id == Zero.Id) return b;
        if (b.Id == Zero.Id) return a;
        return New(Operation.Add, a, b, Value(a).Add(Value(b)));
    }
    internal Number Negate(Number a)
    {
        Node node = _nodes[a.Id];
        if (node.Op == Operation.Multiply && Value(node.Left).IsExact && Value(node.Left).Value == -1)
            return node.Right;
        return Multiply(Constant(-1), a);
    }
    internal Number Subtract(Number a, Number b) => a.Id == b.Id ? Zero : Add(a, Negate(b));
    internal Number Multiply(Number a, Number b)
    {
        if (a.Id == Zero.Id || b.Id == Zero.Id) return Zero;
        if (a.Id == One.Id) return b;
        if (b.Id == One.Id) return a;
        if (a.Id == b.Id && _nodes[a.Id].Op == Operation.Root) return _nodes[a.Id].Left;
        FeatureNumber value = a.Id == b.Id && !Value(a).IsExact ? FeatureNumber.Enclosed(Bounds(a).Square()) :
            Value(a).Multiply(Value(b), exactZeroProducts: true);
        return New(Operation.Multiply, a, b, value);
    }
    internal Number Square(Number a) => Multiply(a, a);
    internal Number Divide(Number a, Number b)
    {
        if (b.Id == One.Id) return a;
        if (a.Id == b.Id && Value(b).Sign is GeometrySign.Positive or GeometrySign.Negative) return One;
        return New(Operation.Divide, a, b, Value(a).Divide(Value(b)));
    }
    internal Number Root(Number value)
    {
        GeometrySign sign = Sign(value);
        if (sign == GeometrySign.Zero) return Zero;
        if (sign != GeometrySign.Positive && (!Bounds(value).IsResolved || Bounds(value).Lower < 0))
            return New(Operation.Root, value, default, default);
        GeometryInterval bounds = GeometryInterval.Enclose(Math.Max(0, Bounds(value).Lower), Bounds(value).Upper).Sqrt();
        FeatureNumber number = FeatureNumber.Enclosed(bounds);
        if (Value(value).IsExact && bounds.IsResolved)
        {
            double root = Math.Sqrt(Value(value).Value);
            if (BoundedGeometryArithmetic.CompareProducts(root, root, Value(value).Value, 1) == GeometrySign.Zero)
                return Constant(root);
        }
        return New(Operation.Root, value, default, number);
    }
    internal Number Abs(Number value) => Sign(value) switch
    {
        GeometrySign.Negative => Negate(value),
        GeometrySign.Zero => Zero,
        GeometrySign.Positive => value,
        _ => Root(Square(value))
    };
    internal Number Max(Number a, Number b)
    {
        Number difference = Subtract(a, b);
        GeometrySign sign = Sign(difference);
        if (sign == GeometrySign.Negative) return b;
        if (sign is GeometrySign.Positive or GeometrySign.Zero) return a;
        // The closed absolute-value formula retains both source branches at an undecided seam.
        GeometryInterval box = GeometryInterval.Enclose(Math.Max(Bounds(a).Lower, Bounds(b).Lower),
            Math.Max(Bounds(a).Upper, Bounds(b).Upper));
        if (box.IsResolved && box.Lower == box.Upper) return Constant(box.Lower);
        Number result = Divide(Add(Add(a, b), Abs(difference)), Constant(2));
        if (_resolved && box.IsResolved)
            _nodes[result.Id] = _nodes[result.Id] with
            {
                Value = FeatureNumber.Enclosed(GeometryInterval.Enclose(
                Math.Max(box.Lower, Bounds(result).Lower), Math.Min(box.Upper, Bounds(result).Upper)))
            };
        return result;
    }
    internal Number Min(Number a, Number b) => Negate(Max(Negate(a), Negate(b)));
    internal Number Clamp(Number value, Number half) => Min(half, Max(Negate(half), value));

    Number New(Operation op, Number a, Number b, FeatureNumber value, double constant = 0)
    {
        if (!value.IsResolved) { _resolved = false; return default; }
        if (value.IsExact) { op = Operation.Constant; constant = value.Value; a = b = default; }
        if (!_resolved || _count == MaximumNodes) { _resolved = false; return default; }
        for (int i = 0; i < _count; i++)
            if (_nodes[i].Op == op && _nodes[i].Left.Id == a.Id && _nodes[i].Right.Id == b.Id &&
                _nodes[i].Constant == constant) return new(i);
        int root = op == Operation.Root ? _count : op == Operation.Constant ? -1 :
            Math.Max(_nodes[a.Id].Root, _nodes[b.Id].Root);
        int id = _count++;
        _nodes[id] = new(op, a, b, root, constant, value);
        return new(id);
    }

    internal GeometrySign Sign(Number number)
    {
        if (!_resolved || !Value(number).IsResolved) return GeometrySign.Unresolved;
        GeometrySign sign = Value(number).Sign;
        if (sign != GeometrySign.Unresolved) return sign;
        SupportQueryDiagnostics.Record(SupportQueryCounter.ExactFallbacks);
        int root = _nodes[number.Id].Root;
        if (root < 0) return PolynomialSign(number);
        Fraction pair = Split(number, root);
        if (!_resolved) return GeometrySign.Unresolved;
        GeometrySign denominator = Sign(pair.D), a = Sign(pair.A), b = Sign(pair.B);
        if (denominator is GeometrySign.Unresolved or GeometrySign.Zero) return GeometrySign.Unresolved;
        if (a == GeometrySign.Unresolved || b == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        GeometrySign result;
        if (b == GeometrySign.Zero) result = a;
        else if (a == GeometrySign.Zero)
        {
            GeometrySign radicand = Sign(_nodes[root].Left);
            result = radicand == GeometrySign.Zero ? GeometrySign.Zero : radicand == GeometrySign.Positive ? b : GeometrySign.Unresolved;
        }
        else if (a == b) result = b;
        else
        {
            Number difference = Subtract(Square(pair.A), Multiply(Square(pair.B), _nodes[root].Left));
            GeometrySign squared = Sign(difference);
            result = squared is GeometrySign.Zero or GeometrySign.Unresolved ? squared :
                squared == GeometrySign.Positive ? a : b;
        }
        return denominator == GeometrySign.Negative ? Reverse(result) : result;
    }

    static GeometrySign Reverse(GeometrySign sign) => sign == GeometrySign.Negative ? GeometrySign.Positive :
        sign == GeometrySign.Positive ? GeometrySign.Negative : sign;

    Fraction Split(Number number, int root)
    {
        if (number.Id == root) return new(Zero, One, One);
        Node node = _nodes[number.Id];
        if (node.Root < root) return new(number, Zero, One);
        Fraction a = Split(node.Left, root), b = Split(node.Right, root);
        Number radicand = _nodes[root].Left;
        if (node.Op == Operation.Add)
        {
            if (a.D.Id == b.D.Id) return new(Add(a.A, b.A), Add(a.B, b.B), a.D);
            return new(Add(Multiply(a.A, b.D), Multiply(b.A, a.D)),
                Add(Multiply(a.B, b.D), Multiply(b.B, a.D)), Multiply(a.D, b.D));
        }
        if (node.Op == Operation.Multiply)
            return new(Add(Multiply(a.A, b.A), Multiply(Multiply(a.B, b.B), radicand)),
                Add(Multiply(a.A, b.B), Multiply(a.B, b.A)), Multiply(a.D, b.D));
        if (node.Op == Operation.Divide)
            return new(Multiply(Subtract(Multiply(a.A, b.A), Multiply(Multiply(a.B, b.B), radicand)), b.D),
                Multiply(Subtract(Multiply(a.B, b.A), Multiply(a.A, b.B)), b.D),
                Multiply(a.D, Subtract(Square(b.A), Multiply(Square(b.B), radicand))));
        _resolved = false;
        return default;
    }

    GeometrySign PolynomialSign(Number number)
    {
        _work.Reset(); Array.Clear(_compiled); _compiledCount = 0;
        _onePolynomial = _work.Constant(1);
        Polynomial? value = Compile(number);
        if (value is not { } polynomial) return GeometrySign.Unresolved;
        GeometrySign n = _work.Sign(polynomial.N), d = _work.Sign(polynomial.D);
        return d is GeometrySign.Zero or GeometrySign.Unresolved ? GeometrySign.Unresolved :
            d == GeometrySign.Negative ? Reverse(n) : n;
    }

    SupportSourcePolynomial? Rent(Operation op, SupportSourcePolynomial? a = null,
        SupportSourcePolynomial? b = null, double constant = 0)
    {
        if (++_compiledCount > 54 || (op == Operation.Multiply && a!.Degree + b!.Degree > 8)) return null;
        return op switch
        {
            Operation.Add => _work.Add(a!, b!),
            Operation.Multiply => _work.Multiply(a!, b!),
            _ => _work.Constant(constant)
        };
    }
    SupportSourcePolynomial? Product(SupportSourcePolynomial a, SupportSourcePolynomial b) =>
        ReferenceEquals(a, _onePolynomial) ? b : ReferenceEquals(b, _onePolynomial) ? a : Rent(Operation.Multiply, a, b);

    Polynomial? Compile(Number number)
    {
        if (_compiled[number.Id] is { } cached) return cached;
        Node node = _nodes[number.Id];

        if (node.Op == Operation.Constant)
        {
            SupportSourcePolynomial? n = Rent(Operation.Constant, constant: node.Constant);
            return n is null ? null : _compiled[number.Id] = new Polynomial(n, _onePolynomial);
        }
        if (Compile(node.Left) is not { } a || Compile(node.Right) is not { } b) return null;
        SupportSourcePolynomial? numerator, denominator;
        if (node.Op == Operation.Add)
        {
            if (ReferenceEquals(a.D, b.D))
            { numerator = Rent(Operation.Add, a.N, b.N); denominator = a.D; }
            else
            {
                var left = Product(a.N, b.D); var right = Product(b.N, a.D);
                if (left is null || right is null) return null;
                numerator = Rent(Operation.Add, left, right); denominator = Product(a.D, b.D);
            }
        }
        else if (node.Op == Operation.Multiply)
        { numerator = Product(a.N, b.N); denominator = Product(a.D, b.D); }
        else if (node.Op == Operation.Divide)
        { numerator = Product(a.N, b.D); denominator = Product(a.D, b.N); }
        else return null;
        return numerator is null || denominator is null ? null :
            _compiled[number.Id] = new Polynomial(numerator, denominator);
    }
}
