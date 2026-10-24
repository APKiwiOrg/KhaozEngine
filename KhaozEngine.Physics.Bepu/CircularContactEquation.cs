namespace KhaozEngine.Physics.Bepu;

internal interface ICircularContactArithmetic<T>
{
    T Add(T a, T b);
    T Subtract(T a, T b);
    T Multiply(T a, T b);
    T Square(T a);
    T Four { get; }
}

/// <summary>The capsule cylinder's original rounded-rim equation. Callers retain the radial
/// domain and the unsquared difference sign before accepting a squared root.</summary>
internal static class CircularContactEquation
{
    internal static T Difference<T, A>(T radialSquared, T radiusSquared, T axialSquared, T marginSquared, A a)
        where A : ICircularContactArithmetic<T> =>
        a.Subtract(a.Add(a.Add(radialSquared, radiusSquared), axialSquared), marginSquared);

    internal static T SquaredDifference<T, A>(T difference, T radiusSquared, T radialSquared, A a)
        where A : ICircularContactArithmetic<T> =>
        a.Subtract(a.Square(difference), a.Multiply(a.Four, a.Multiply(radiusSquared, radialSquared)));
}

internal readonly struct CircularSweepArithmetic(SweepExactWorkspace workspace) : ICircularContactArithmetic<int>
{
    public int Add(int a, int b) => workspace.Add(a, b);
    public int Subtract(int a, int b) => workspace.Subtract(a, b);
    public int Multiply(int a, int b) => workspace.Multiply(a, b);
    public int Square(int a) => workspace.Square(a);
    public int Four => workspace.Constant(4);
}

internal readonly struct CircularPolynomialArithmetic(ConvexPolynomialWorkspace workspace) : ICircularContactArithmetic<ConvexPolynomial>
{
    public ConvexPolynomial Add(ConvexPolynomial a, ConvexPolynomial b) => a.Subtract(b.Scale(-ConvexRational.One));
    public ConvexPolynomial Subtract(ConvexPolynomial a, ConvexPolynomial b) => a.Subtract(b);
    public ConvexPolynomial Multiply(ConvexPolynomial a, ConvexPolynomial b) => a.Multiply(b);
    public ConvexPolynomial Square(ConvexPolynomial a) => a.Multiply(a);
    public ConvexPolynomial Four => workspace.Constant(ConvexRational.From(4));
}
