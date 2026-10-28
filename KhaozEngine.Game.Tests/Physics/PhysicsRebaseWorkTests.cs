using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Physics.Bepu;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Machine-independent rebase guards. The source contract bounds engine calls, not Bepu's internal
/// native tree visits. Those can return identical state with zero managed allocation despite extra work and
/// require the separate hosted timing budget. No production counter is added solely to observe a test.</summary>
[Collection("AllocSensitive")]
public sealed class PhysicsRebaseWorkTests
{
    /// <summary>A rebase walks unmanaged buffers and must allocate nothing managed. Isolation and the checked
    /// no-GC region prevent concurrent collections from perturbing per-thread allocation accounting.</summary>
    [Fact]
    public void Rebase_AllocatesNothing_AtAResidentStreamingScale()
    {
        using BepuPhysicsWorld world = PhysicsRebaseResidentWorld.CreateWarmed();
        long allocated = AllocationMeasurement.Measure("two warmed rebases", () =>
        {
            world.Rebase(PhysicsRebaseResidentWorld.Shift);
            world.Rebase(Vector3.Zero);
        });
        Assert.True(allocated == 0, $"Two warmed rebases allocated {allocated} managed bytes. Expected zero.");
    }

    /// <summary>Exactly one call per body in every allocated set, including sleepers and world anchors, and one
    /// per static. Pin the loop bounds as well as call sites so a nested or repeated refit cannot hide behind a
    /// single syntactic call. This deliberately says nothing about work inside Bepu's UpdateBounds.</summary>
    [Fact]
    public void Rebase_RefitsEachBodyAndStaticExactlyOnce()
    {
        BlockSyntax body = RebaseBody();
        ForStatementSyntax[] loops = body.DescendantNodes().OfType<ForStatementSyntax>().ToArray();
        Assert.Equal(3, loops.Length);
        AssertLoop(loops[0], "int setIndex = 0", "setIndex < _sim.Bodies.Sets.Length", "setIndex++");
        AssertLoop(loops[1], "int i = 0", "i < set.Count", "i++");
        AssertLoop(loops[2], "int i = _sim.Statics.Count - 1", "i >= 0", "i--");
        InvocationExpressionSyntax[] calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        InvocationExpressionSyntax dynamicRefit = Assert.Single(calls, c => Compact(c.Expression) == "body.UpdateBounds");
        InvocationExpressionSyntax staticRefit = Assert.Single(calls, c => Compact(c.Expression) == "_sim.Statics.UpdateBounds");
        Assert.Same(loops[1], dynamicRefit.Parent!.Parent!.Parent);
        Assert.Same(loops[2], staticRefit.Parent!.Parent!.Parent);
        Assert.Equal(new[] { loops[1], loops[0] }, dynamicRefit.Ancestors().OfType<ForStatementSyntax>());
        Assert.Equal(new[] { loops[2] }, staticRefit.Ancestors().OfType<ForStatementSyntax>());
        // A helper detour could conceal an additional traversal or refit. New calls require explicit review.
        Assert.All(calls, c => Assert.Contains(Compact(c.Expression), new[]
        {
            "EnterMutation", "BeginSweepMutation", "CompleteSweepMutation", "_installedSupportGeometry.Clear",
            "body.UpdateBounds", "_sim.Statics.UpdateBounds",
        }));
    }

    /// <summary>Statics.ApplyDescription wakes sleepers overlapping either old or new bounds. Direct pose
    /// writes plus UpdateBounds preserve sleep state. Prohibit the API even if a fixture has no overlap.</summary>
    [Fact]
    public void Rebase_DoesNotApplyStaticDescriptions()
    {
        Assert.DoesNotContain(RebaseBody().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "ApplyDescription");
    }

    static void AssertLoop(ForStatementSyntax loop, string declaration, string condition, string increment)
    {
        Assert.Equal(declaration.Replace(" ", ""), Compact(loop.Declaration!));
        Assert.Equal(condition.Replace(" ", ""), Compact(loop.Condition!));
        Assert.Equal(increment, Compact(Assert.Single(loop.Incrementors)));
    }

    static string Compact(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));

    static BlockSyntax RebaseBody([CallerFilePath] string source = "")
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
        SyntaxNode tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,
            "KhaozEngine.Physics.Bepu", "BepuPhysicsWorld.Rebase.cs"))).GetRoot();
        return Assert.Single(tree.DescendantNodes().OfType<MethodDeclarationSyntax>(), m => m.Identifier.ValueText == "Rebase").Body!;
    }
}
