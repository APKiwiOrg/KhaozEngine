using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportCostScratchTests
{
    [Fact]
    public void ScratchIncludesTheCompleteFootprintOwner()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        FieldInfo field = typeof(BepuPhysicsWorld).GetField("_footprintScratch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? footprint = field.GetValue(world);
        Assert.NotNull(footprint);
        long complete = SupportCostScratch.PayloadBytes(world);
        long without;
        try
        {
            field.SetValue(world, null);
            without = SupportCostScratch.PayloadBytes(world);
        }
        finally { field.SetValue(world, footprint); }
        Assert.True(complete > without, $"Complete {complete}, without footprint {without}.");
    }
}
