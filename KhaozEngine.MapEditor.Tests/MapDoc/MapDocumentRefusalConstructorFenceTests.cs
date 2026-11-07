using System;
using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDocumentRefusalConstructorFenceTests
{
    static readonly ConstructorInfo[] MessageOnlyConstructors =
    {
        typeof(MapDocumentException).GetConstructor(new[] { typeof(string) })!,
        typeof(MapDocumentException).GetConstructor(new[] { typeof(string), typeof(Exception) })!,
    };

    [Fact]
    public void Engine_callers_always_supply_a_reason()
    {
        // The shared metadata reader scans every method, including compiler-generated lambda bodies.
        var callers = RefusalConstructorFence.EngineCallers(new[]
        {
            typeof(MapDocumentException).Assembly,
            typeof(MapPhysicsRegistration).Assembly,
            typeof(KhaozEngine.MapDoc.Render3D.NativeMapView).Assembly,
            typeof(EditorDocument).Assembly,
            typeof(MapEditSession).Assembly,
            typeof(MapAssetManifestAdapter).Assembly,
        }, MessageOnlyConstructors);

        Assert.Empty(callers);
    }

    [Theory]
    [InlineData(typeof(MessageOnlyControl))]
    [InlineData(typeof(MessageAndInnerControl))]
    [InlineData(typeof(LambdaMessageOnlyControl))]
    [InlineData(typeof(LambdaMessageAndInnerControl))]
    public void Fence_finds_each_forbidden_constructor_including_target_typed_lambdas(Type control)
    {
        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(MapDocumentRefusalConstructorFenceTests).Assembly }, MessageOnlyConstructors);

        Assert.Single(callers, caller => caller.StartsWith(control.FullName + ".", StringComparison.Ordinal)
            || caller.StartsWith(control.FullName + "+", StringComparison.Ordinal));
        Assert.DoesNotContain($"{typeof(ReasonControl).FullName}.{nameof(ReasonControl.Build)}", callers);
    }

    public static class MessageOnlyControl
    {
        public static MapDocumentException Build() => new("Constructor fence positive control.");
    }

    public static class MessageAndInnerControl
    {
        public static MapDocumentException Build() =>
            new("Constructor fence positive control.", new InvalidOperationException());
    }

    public static class LambdaMessageOnlyControl
    {
        public static Func<MapDocumentException> Build() => () => new("Constructor fence positive control.");
    }

    public static class LambdaMessageAndInnerControl
    {
        public static Func<MapDocumentException> Build() => () =>
            new("Constructor fence positive control.", new InvalidOperationException());
    }

    public static class ReasonControl
    {
        public static MapDocumentException Build() =>
            new(MapDocumentRefusal.SchemaValueRequired, "Allowed reason-taking constructor.");
    }
}
