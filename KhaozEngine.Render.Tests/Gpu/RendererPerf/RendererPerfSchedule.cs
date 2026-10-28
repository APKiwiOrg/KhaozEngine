using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfScheduledRecipe(int Scene, string Version, string Profile)
{
    internal string SceneId => $"S{Scene}";
}

internal sealed record RendererPerfProfilePair(int BaselineScene, int CandidateScene, string Label);

// One admitted preset. A manifest must reproduce this schedule exactly, so the table is the single
// source of every preset's scenes, frame counts, controls, conditioning scene and hosted legs.
internal sealed record RendererPerfSchedule(string Preset, RendererPerfExecutionClass ExecutionClass,
    ImmutableArray<RendererPerfScheduledRecipe> Recipes, int Iterations, int WarmupFrames, int MeasuredFrames,
    ImmutableArray<int> ControlIds, int ControlWarmupFrames, int ControlMeasuredFrames, int ConditioningFrames,
    int ConditioningScene, int TotalFrames, ImmutableArray<string> Legs)
{
    internal ImmutableArray<RendererPerfProfilePair> ProfilePairs { get; init; } = [];
    internal string ConditioningSceneId => $"S{ConditioningScene}";
    internal string ConditioningBlockId => "conditioning:" + ConditioningSceneId;
    internal string ConditioningOperation => ConditioningBlockId + "/TotalOnly/0";

    // Each iteration rotates the scene order by one and alternates which layout runs first, so neither
    // scene position nor layout order is confounded with time in the unit.
    internal ImmutableArray<RendererPerfBlock> Blocks()
    {
        var blocks = ImmutableArray.CreateBuilder<RendererPerfBlock>(Iterations * Recipes.Length * 2);
        for (int iteration = 0; iteration < Iterations; iteration++)
            for (int ordinal = 0; ordinal < Recipes.Length; ordinal++)
            {
                var recipe = Recipes[(ordinal + iteration) % Recipes.Length];
                bool totalFirst = Recipes.Length == 1 || (recipe.Scene + iteration) % 2 == 0;
                for (int layout = 0; layout < 2; layout++)
                    blocks.Add(new(recipe.SceneId, (layout == 0) == totalFirst ? RendererPerfLayout.TotalOnly : RendererPerfLayout.PerPass,
                        iteration, WarmupFrames, MeasuredFrames));
            }
        return blocks.MoveToImmutable();
    }

    internal int Operations => 1 + Blocks().Length + ControlIds.Length;
}

internal static class RendererPerfSchedules
{
    static readonly ImmutableArray<string> AllLegs = ["linux-vulkan", "windows-d3d11", "windows-vulkan"];
    static readonly ImmutableArray<string> PilotLegs = ["linux-vulkan", "windows-d3d11"];
    static readonly ImmutableArray<int> PilotControls = [1, 4, 5, 7, 9, 11];
    static readonly ImmutableArray<int> AllControls = [.. Enumerable.Range(1, 12)];
    static readonly ImmutableArray<RendererPerfScheduledRecipe> V1Recipes =
    [
        new(0, "empty/1", "empty-native-1080/1"),
        new(1, "village/1", "renderer-full-native-1080/1"),
        new(2, "outdoors/1", "renderer-full-native-1080/1"),
        new(3, "crowd/1", "renderer-full-native-1080/1"),
        new(4, "effects-ui/1", "renderer-full-native-1080/1"),
    ];
    static readonly ImmutableArray<RendererPerfScheduledRecipe> V1Pilot = [V1Recipes[3]];
    static readonly ImmutableArray<RendererPerfScheduledRecipe> WaterOpticsV2Recipes =
        [.. RendererPerfWaterOptics.RecipeIds.Select((id, scene) => new RendererPerfScheduledRecipe(scene, id + "/1", "renderer-full-native-1080/1"))];
    static readonly ImmutableArray<RendererPerfProfilePair> WaterOpticsV2Pairs =
    [
        new(0, 1, "village OFF to M2 ON"), new(1, 2, "village M2 ON to M5 ON"),
        new(3, 4, "outdoors OFF to M2 ON"), new(4, 5, "outdoors M2 ON to M5 ON")
    ];

    static RendererPerfSchedule V1PilotSchedule(string preset, RendererPerfExecutionClass executionClass)
        => new(preset, executionClass, V1Pilot, 1, 64, 128, PilotControls, 32, 64, 300, 3, 1260, PilotLegs);
    static RendererPerfSchedule V1FullSchedule(string preset, RendererPerfExecutionClass executionClass)
        => new(preset, executionClass, V1Recipes, 3, 192, 256, AllControls, 64, 128, 1200, 3, 16944, AllLegs);

    static readonly ImmutableArray<RendererPerfSchedule> Table =
    [
        V1PilotSchedule("pilot", RendererPerfExecutionClass.Performance),
        V1FullSchedule("full", RendererPerfExecutionClass.Performance),
        V1PilotSchedule("rehearsal", RendererPerfExecutionClass.Rehearsal),
        V1FullSchedule("rehearsal-full", RendererPerfExecutionClass.Rehearsal),
        // A short conditioning session still carries the cold first frame and the S3 recipe facts without scene blocks.
        new("rehearsal-controls", RendererPerfExecutionClass.Rehearsal, V1Pilot, 0, 0, 0, AllControls, 64, 128, 3, 3, 2307, AllLegs),
        new("water-optics-v2", RendererPerfExecutionClass.Performance, WaterOpticsV2Recipes,
            3, 192, 256, AllControls, 64, 128, 1200, 5, 19632, PilotLegs) { ProfilePairs = WaterOpticsV2Pairs },
        new("rehearsal-water-optics-v2", RendererPerfExecutionClass.Rehearsal, WaterOpticsV2Recipes,
            3, 64, 128, AllControls, 32, 64, 300, 5, 8364, ["linux-vulkan"]) { ProfilePairs = WaterOpticsV2Pairs },
        new("rehearsal-water-optics-v2-single", RendererPerfExecutionClass.Rehearsal, WaterOpticsV2Recipes,
            1, 32, 64, AllControls, 32, 64, 300, 5, 2604, ["windows-d3d11"]) { ProfilePairs = WaterOpticsV2Pairs },
    ];

    internal static ImmutableArray<string> Presets { get; } = [.. Table.Select(s => s.Preset)];
    // Only manifest-free decoding uses this envelope. Admission retains the selected schedule's exact bound.
    internal static int MaximumOperations { get; } = Table.Max(s => s.Operations);

    internal static RendererPerfSchedule For(string preset)
        => Table.FirstOrDefault(s => s.Preset == preset)
            ?? throw new RendererPerfProtocolException("Preset", "Unknown renderer preset.");
}
