using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// One campaign owns its recipe order, profile pairs, paid envelope and the two rehearsal legs.
internal sealed record RendererPerfWaterOpticsCampaign(string Preset,
    ImmutableArray<RendererPerfWaterOpticsProfile> Profiles, ImmutableArray<RendererPerfProfilePair> Pairs,
    decimal MaximumUsd, ImmutableArray<string> Legs)
{
    internal string RehearsalPreset => "rehearsal-" + Preset;
    internal string SinglePreset => RehearsalPreset + "-single";
    internal ImmutableArray<string> RecipeIds { get; } = [.. new[] { "village", "outdoors" }
        .SelectMany(scene => Profiles.Select(profile => RendererPerfWaterOptics.RecipeId(scene, profile, Preset)))];
    internal bool ContainsPreset(string preset) => preset == Preset || preset == RehearsalPreset || preset == SinglePreset;
}

internal static class RendererPerfWaterOpticsCampaigns
{
    internal static readonly ImmutableArray<RendererPerfWaterOpticsCampaign> All =
    [
        new("water-optics-v2", RendererPerfWaterOptics.Profiles,
            [new(0, 1, "village OFF to M2 ON"), new(1, 2, "village M2 ON to M5 ON"),
             new(3, 4, "outdoors OFF to M2 ON"), new(4, 5, "outdoors M2 ON to M5 ON")],
            RendererPerfPaidPolicy.WaterOpticsV2CampaignMaximumUsd, ["linux-vulkan", "windows-d3d11"]),
        new("water-optics-v3", [RendererPerfWaterOpticsProfile.Off, RendererPerfWaterOpticsProfile.M5On, RendererPerfWaterOpticsProfile.M3On],
            [new(0, 1, "village OFF to M5 ON"), new(1, 2, "village M5 ON to M3 ON"),
             new(3, 4, "outdoors OFF to M5 ON"), new(4, 5, "outdoors M5 ON to M3 ON")],
            RendererPerfPaidPolicy.WaterOpticsV3CampaignMaximumUsd, ["linux-vulkan", "windows-d3d11"]),
    ];

    internal static RendererPerfWaterOpticsCampaign? ForPreset(string preset) => All.FirstOrDefault(c => c.ContainsPreset(preset));

    internal static RendererPerfRecipe? CreateRecipe(string id)
    {
        foreach (var campaign in All)
        {
            int index = campaign.RecipeIds.IndexOf(id);
            if (index < 0) continue;
            var profile = campaign.Profiles[index % campaign.Profiles.Length];
            return index < campaign.Profiles.Length
                ? new RendererPerfWaterOpticsVillageScene(profile, campaign.Preset)
                : new RendererPerfWaterOpticsOutdoorsScene(profile, campaign.Preset);
        }
        return null;
    }
}
