using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

// SkipLocalsInit only takes effect on the method that owns the stackalloc. These are the four audited kernels, so the
// attribute landing on Edge, Dot or a caller instead leaves the matching row red. Only positive rows are asserted,
// because a method without locals reports InitLocals false with no attribute.
public sealed class CoverageGeometryStackInitTests
{
    [Theory]
    [InlineData("Bilinear")]
    [InlineData("ProjectedArea")]
    [InlineData("SquaredDistanceWithinBudget")]
    [InlineData("Difference")]
    public void Stack_buffer_kernel_skips_local_initialization(string kernel)
    {
        Type point = typeof(CoverageAffinePoint);
        MethodInfo method = kernel switch
        {
            "Bilinear" => Find(kernel, point, point, point, point, typeof(bool)),
            "ProjectedArea" => Find(kernel, point, point, point, typeof(Vector3)),
            "SquaredDistanceWithinBudget" => Find(kernel, point, point, typeof(float)),
            _ => Find(kernel, typeof(CoverageCoordinate), typeof(CoverageCoordinate)),
        };
        MethodBody body = method.GetMethodBody() ?? throw new InvalidOperationException($"{method} has no IL body");
        Assert.NotEmpty(body.LocalVariables);
        Assert.False(body.InitLocals, $"{method} still zero-initializes its locals");
    }

    static MethodInfo Find(string name, params Type[] parameters) =>
        typeof(CoverageGeometry).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, parameters)
            ?? throw new MissingMethodException(nameof(CoverageGeometry), name);
}
