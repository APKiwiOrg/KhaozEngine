using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.Showcase;

/// <summary>Shared geometry for the visible stair treads and their exact collision boxes.</summary>
internal readonly record struct Room3DStep(Vector3 Center, Vector3 HalfExtents)
{
    internal Matrix4x4 World => Matrix4x4.CreateScale(HalfExtents * 2f)
        * Matrix4x4.CreateTranslation(Center);
}

/// <summary>Authored fixtures that expose step-climb and cascade transitions in the overworld.</summary>
internal static class Room3DTestbed
{
    internal const float PlatformHeight = 2f;
    const int StepCount = 8;
    const float TreadDepth = 0.8f;
    const float PlatformFront = 9.5f;

    internal static IReadOnlyList<Room3DStep> CreateSteps(Func<float, float, float> groundHeight)
    {
        var steps = new Room3DStep[StepCount];
        float baseY = groundHeight(0f, 12f);
        float startZ = PlatformFront - StepCount * TreadDepth;
        for (int i = 0; i < StepCount; i++)
        {
            float height = (i + 1) * (PlatformHeight / StepCount);
            steps[i] = new Room3DStep(new Vector3(0f, baseY + height * 0.5f,
                startZ + (i + 0.5f) * TreadDepth), new Vector3(1.5f, height * 0.5f, TreadDepth * 0.5f));
        }
        return steps;
    }

    internal static IReadOnlyList<PropPlacement> CreateTreeLine(Vector3 eye, Vector3 target,
        float handoffDepth, Func<float, float, float> groundHeight)
    {
        Vector3 forward = Vector3.Normalize(target - eye);
        Vector3 horizontal = new(forward.X, 0f, forward.Z);
        float horizontalLength = horizontal.Length();
        if (horizontalLength < 0.01f) return Array.Empty<PropPlacement>();
        horizontal /= horizontalLength;
        Vector3 across = Vector3.Cross(horizontal, Vector3.UnitY);
        var trees = new PropPlacement[6];
        for (int i = 0; i < trees.Length; i++)
        {
            float desiredDepth = handoffDepth + (i % 2 == 0 ? -1.5f : 1.5f);
            Vector3 p = eye + horizontal * handoffDepth + across * ((i - 2.5f) * 4f);
            // Seat the authored line on the terrain while keeping it around the initial view-depth split.
            for (int iteration = 0; iteration < 4; iteration++)
            {
                p.Y = groundHeight(p.X, p.Z);
                p += horizontal * ((desiredDepth - Vector3.Dot(p - eye, forward)) / horizontalLength);
            }
            trees[i] = new PropPlacement("pine_a", p.X, groundHeight(p.X, p.Z), p.Z, 1f, 0f, 0);
        }
        return trees;
    }
}
