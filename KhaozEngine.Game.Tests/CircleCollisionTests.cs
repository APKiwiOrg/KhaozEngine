using KhaozEngine.Collision;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests;

// A plain circle collider with no precise refinement.
file sealed class Circle : ICircleCollider
{
    public Vector2 Position { get; init; }
    public float Radius { get; init; }
}

// A collider that opts into per-pixel precise refinement; the predicate is supplied by the test.
file sealed class PreciseCircle : ICircleCollider, IPreciseCircleCollisionTarget
{
    public Vector2 Position { get; init; }
    public float Radius { get; init; }
    public System.Func<Vector2, float, bool> Precise { get; init; } = (_, _) => true;

    public bool IntersectsCircle(Vector2 center, float radius) => Precise(center, radius);
}

public class CircleCollisionTests
{
    [Fact]
    public void IntersectsTrueWhenCirclesOverlap()
    {
        Assert.True(CircleCollision.Intersects(new Vector2(0f, 0f), 2f, new Vector2(3f, 0f), 2f));
    }

    [Fact]
    public void IntersectsTrueWhenExactlyTouching()
    {
        // distance == combined radius -> the original uses <=, so touching counts as intersecting.
        Assert.True(CircleCollision.Intersects(new Vector2(0f, 0f), 2f, new Vector2(5f, 0f), 3f));
    }

    [Fact]
    public void IntersectsFalseWhenJustBeyondTouching()
    {
        Assert.False(CircleCollision.Intersects(new Vector2(0f, 0f), 2f, new Vector2(5.0001f, 0f), 3f));
    }

    [Fact]
    public void IntersectsCollidersUsesPositionAndRadius()
    {
        var a = new Circle { Position = new Vector2(0f, 0f), Radius = 2f };
        var b = new Circle { Position = new Vector2(3f, 0f), Radius = 2f };
        Assert.True(CircleCollision.Intersects(a, b));
    }

    [Fact]
    public void DoCollidersCollideFalseWhenCirclesMiss()
    {
        var a = new Circle { Position = new Vector2(0f, 0f), Radius = 1f };
        var b = new Circle { Position = new Vector2(100f, 0f), Radius = 1f };
        Assert.False(CircleCollision.DoCollidersCollide(a, b));
    }

    [Fact]
    public void DoCollidersCollideTrueWhenCirclesOverlapAndNeitherIsPrecise()
    {
        var a = new Circle { Position = new Vector2(0f, 0f), Radius = 2f };
        var b = new Circle { Position = new Vector2(3f, 0f), Radius = 2f };
        Assert.True(CircleCollision.DoCollidersCollide(a, b));
    }

    [Fact]
    public void DoCollidersCollideFalseWhenPreciseSourceRejects()
    {
        var source = new PreciseCircle { Position = new Vector2(0f, 0f), Radius = 5f, Precise = (_, _) => false };
        var target = new Circle { Position = new Vector2(1f, 0f), Radius = 5f };
        Assert.False(CircleCollision.DoCollidersCollide(source, target));
    }

    [Fact]
    public void DoCollidersCollideFalseWhenPreciseTargetRejects()
    {
        var source = new Circle { Position = new Vector2(0f, 0f), Radius = 5f };
        var target = new PreciseCircle { Position = new Vector2(1f, 0f), Radius = 5f, Precise = (_, _) => false };
        Assert.False(CircleCollision.DoCollidersCollide(source, target));
    }

    [Fact]
    public void DoCollidersCollideTrueWhenBothPreciseAccept()
    {
        var source = new PreciseCircle { Position = new Vector2(0f, 0f), Radius = 5f, Precise = (_, _) => true };
        var target = new PreciseCircle { Position = new Vector2(1f, 0f), Radius = 5f, Precise = (_, _) => true };
        Assert.True(CircleCollision.DoCollidersCollide(source, target));
    }

    [Fact]
    public void DoCollidersCollidePreciseTargetReceivesSourcePositionAndRadius()
    {
        Vector2 seenCenter = default;
        float seenRadius = 0f;
        var source = new Circle { Position = new Vector2(7f, 8f), Radius = 3f };
        var target = new PreciseCircle
        {
            Position = new Vector2(7f, 8f),
            Radius = 3f,
            Precise = (center, radius) => { seenCenter = center; seenRadius = radius; return true; },
        };

        CircleCollision.DoCollidersCollide(source, target);

        Assert.Equal(new Vector2(7f, 8f), seenCenter);
        Assert.Equal(3f, seenRadius);
    }

    [Fact]
    public void DoCollidersCollideNonPreciseSourceOverloadHonorsPreciseTarget()
    {
        var target = new PreciseCircle { Position = new Vector2(1f, 0f), Radius = 5f, Precise = (_, _) => false };
        Assert.False(CircleCollision.DoCollidersCollide(new Vector2(0f, 0f), 5f, target));
        var accepting = new PreciseCircle { Position = new Vector2(1f, 0f), Radius = 5f, Precise = (_, _) => true };
        Assert.True(CircleCollision.DoCollidersCollide(new Vector2(0f, 0f), 5f, accepting));
    }

    [Fact]
    public void DoCollidersCollideNonPreciseTargetOverloadHonorsPreciseSource()
    {
        var source = new PreciseCircle { Position = new Vector2(0f, 0f), Radius = 5f, Precise = (_, _) => false };
        Assert.False(CircleCollision.DoCollidersCollide(source, new Vector2(1f, 0f), 5f));
        var accepting = new PreciseCircle { Position = new Vector2(0f, 0f), Radius = 5f, Precise = (_, _) => true };
        Assert.True(CircleCollision.DoCollidersCollide(accepting, new Vector2(1f, 0f), 5f));
    }

    [Fact]
    public void DoCollidersCollideNonPreciseTargetOverloadFalseWhenCirclesMiss()
    {
        var source = new Circle { Position = new Vector2(0f, 0f), Radius = 1f };
        Assert.False(CircleCollision.DoCollidersCollide(source, new Vector2(100f, 0f), 1f));
    }
}
