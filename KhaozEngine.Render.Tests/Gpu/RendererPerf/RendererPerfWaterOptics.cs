using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>The opt-in water optics a v2 recipe measures. OFF draws neither, M2 ON draws underwater sunlight and
/// M5 ON adds the water column light.</summary>
internal enum RendererPerfWaterOpticsProfile { Off, M2On, M5On }

/// <summary>Profile ids, the approved water optics values and the v2 recipe ids in scene order.</summary>
internal static class RendererPerfWaterOptics
{
    internal static readonly Color WaterColor = new(0.28f, 0.30f, 0.18f, 1f);
    internal const float Clarity = 1.5f;
    internal const float UnderwaterPenumbraPerMetre = 0.03f;

    internal static readonly ImmutableArray<RendererPerfWaterOpticsProfile> Profiles =
        [RendererPerfWaterOpticsProfile.Off, RendererPerfWaterOpticsProfile.M2On, RendererPerfWaterOpticsProfile.M5On];

    /// <summary>S0 to S5: the village pool under each profile, then the outdoors pool under each profile.</summary>
    internal static readonly ImmutableArray<string> RecipeIds =
    [
        RecipeId("village", RendererPerfWaterOpticsProfile.Off),
        RecipeId("village", RendererPerfWaterOpticsProfile.M2On),
        RecipeId("village", RendererPerfWaterOpticsProfile.M5On),
        RecipeId("outdoors", RendererPerfWaterOpticsProfile.Off),
        RecipeId("outdoors", RendererPerfWaterOpticsProfile.M2On),
        RecipeId("outdoors", RendererPerfWaterOpticsProfile.M5On),
    ];

    /// <summary>A nested id, so its frozen file lives at <c>recipes/water-optics-v2/scene/profile.json</c>.</summary>
    internal static string RecipeId(string scene, RendererPerfWaterOpticsProfile profile) =>
        "water-optics-v2/" + scene + "/" + Id(profile);

    internal static string Id(RendererPerfWaterOpticsProfile profile) => profile switch
    {
        RendererPerfWaterOpticsProfile.Off => "off",
        RendererPerfWaterOpticsProfile.M2On => "m2-on",
        RendererPerfWaterOpticsProfile.M5On => "m5-on",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>The direct sun absorption both opt-ins apply. M2 reads it from AbsorptionPerMetre and M5 derives it
    /// from WaterColor and Clarity, so setting it to the derived extinction makes their direct light match.</summary>
    internal static Color DerivedAbsorption()
    {
        var extinction = WaterColumnOptics.Derive(WaterColor, Clarity).Extinction;
        return new Color(extinction.X, extinction.Y, extinction.Z, 0f);
    }

    internal static void Apply(WaterSettings water, RendererPerfWaterOpticsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(water);
        water.WaterColor = WaterColor;
        water.Clarity = Clarity;
        // Equal to the engine default, set explicitly so the approval holds if the default moves.
        water.UnderwaterPenumbraPerMetre = UnderwaterPenumbraPerMetre;
        water.AbsorptionPerMetre = DerivedAbsorption();
        water.UnderwaterSunlight = profile is RendererPerfWaterOpticsProfile.M2On or RendererPerfWaterOpticsProfile.M5On;
        water.WaterColumnLight = profile is RendererPerfWaterOpticsProfile.M5On;
    }
}

/// <summary>A v2 water optics recipe: one pool under one profile.</summary>
internal interface IRendererPerfWaterOpticsScene
{
    RendererPerfWaterOpticsProfile Profile { get; }
    RendererPerfWaterOpticsPool Pool { get; }
    /// <summary>The recipe's resolved water settings, the object a block copies into the scene.</summary>
    WaterSettings WaterSettings { get; }
}

/// <summary>The frozen Expected block of a v2 recipe. The v1 fields keep their meaning, with terrain visibility
/// from the engine's culling rule because the v2 cameras frame the pool and cull part of the land. Rigid
/// visibility moves by a few draws as the camera drifts, so it is a range over the whole sequence.</summary>
internal static class RendererPerfWaterOpticsExpected
{
    internal static object Create(IReadOnlyList<RendererPerfRigidBounds> terrain, IReadOnlyList<RendererPerfRigidBounds> rigid,
        int buildings, int props, int foliage, int water, IReadOnlyList<Matrix4x4> viewProjections)
    {
        RendererPerfVisibleRange visibleTerrain = RendererPerfMainPassVisibility.Range(terrain, viewProjections);
        if (visibleTerrain.Min != visibleTerrain.Max)
            throw new InvalidOperationException("The v2 terrain visibility changes within the frame sequence.");
        RendererPerfVisibleRange visibleRigid = RendererPerfMainPassVisibility.Range(rigid, viewProjections);
        return new
        {
            TerrainIntended = terrain.Count,
            TerrainSubmitted = terrain.Count,
            TerrainVisible = visibleTerrain.Min,
            TerrainCulled = terrain.Count - visibleTerrain.Min,
            BuildingComponents = buildings,
            PropInstances = props,
            ActorIntended = 0,
            ActorVisible = 0,
            ActorCulled = 0,
            Foliage = foliage,
            Water = water,
            Effects = 0,
            Ui = 0,
            RigidIntended = rigid.Count,
            RigidVisibleMin = visibleRigid.Min,
            RigidVisibleMax = visibleRigid.Max,
            RigidCulledMin = rigid.Count - visibleRigid.Max,
            RigidCulledMax = rigid.Count - visibleRigid.Min,
            Culling = "engine-main-pass-rule-over-all-sequence-frames",
        };
    }
}
