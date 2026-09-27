using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>Facts for the payload builder storage owned by each generator.</summary>
public class ItemGeneratorPayloadReuseTests
{
    [Fact]
    public void A_generator_reused_after_a_unique_matches_a_fresh_rare_byte_for_byte()
    {
        GenerationContext rare = new(
            GenerationWorld.Greatsword,
            ItemLevel: 60,
            ForcedRarityId: GenerationWorld.RareRarity,
            ForcedUniqueTemplateId: 0,
            Quality: 0);
        GenerationResult expected = GenerationWorld.Generator(new SeededRandomSource(11)).Generate(rare);

        ItemGenerator reused = GenerationWorld.Generator(new SeededRandomSource(11));
        _ = reused.Generate(new GenerationContext(
            GenerationWorld.Greatsword,
            ItemLevel: 60,
            ForcedRarityId: GenerationWorld.RareRarity,
            ForcedUniqueTemplateId: GenerationWorld.SunbrandTemplate,
            Quality: 0));
        GenerationResult actual = reused.Generate(rare);

        Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());
    }
}
