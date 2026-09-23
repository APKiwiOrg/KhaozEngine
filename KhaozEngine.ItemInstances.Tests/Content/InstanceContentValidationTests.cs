using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The twelve checks of spec 8.9, the two weight bounds of
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/944">944</see> and the two candidate table
/// ceilings, one fact per code, plus the
/// three facts about the band as a whole: the publish-only skip, a clean authored set, and the EMPTY set
/// that proves these are shapes rather than one game's content.
/// <para>
/// A check fact runs the band's own sweep directly rather than through
/// <see cref="ContentValidator.Validate"/>, so the fact reads as the one defect it is about and no Scope A
/// finding stands beside it. One fact does go through the full sweep, because pass 6 being WIRED is the
/// other half of the claim and a check nobody reaches is not a check.
/// </para>
/// <para>
/// The publish-only checks take the previous snapshot as an ARGUMENT, which is what keeps one
/// implementation honest across boot, publish and here.
/// </para>
/// </summary>
public class InstanceContentValidationTests
{
    [Fact]
    public void A_child_row_whose_parent_reference_does_not_resolve_is_KEC0100()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(registry, Mod(registry, 1, "sharp"), ModTier(registry, 1, "sharp_t1", modId: 9, ordinal: 1));

        List<ContentFinding> findings = Band(candidate);

        ContentFinding finding = Only(findings, InstanceContentFindings.ParentUnresolved);
        Assert.Equal(InstanceContentTypeIds.ModTierTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
        Assert.Contains("mod_id", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_currency_guard_naming_a_step_of_a_DIFFERENT_currency_is_KEC0100()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Currency(registry, 1, "whetstone"),
            Currency(registry, 2, "grindstone"),
            Step(registry, 1, "whetstone_1", currencyId: 1),
            Guard(registry, 1, "grindstone_g1", currencyId: 2, stepId: 1));

        List<ContentFinding> findings = Band(candidate);

        ContentFinding finding = Only(findings, InstanceContentFindings.ParentUnresolved);
        Assert.Equal(InstanceContentTypeIds.CurrencyGuardTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
        Assert.Contains("currency_step_id", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mod_tier_whose_item_level_min_exceeds_its_item_level_max_is_KEC0101()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1, itemLevelMin: 80, itemLevelMax: 60));

        List<ContentFinding> findings = Band(candidate);

        ContentFinding finding = Only(findings, InstanceContentFindings.TierItemLevelRange);
        Assert.Equal(InstanceContentTypeIds.ModTierTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
    }

    [Fact]
    public void A_mod_tier_ordinal_outside_1_to_255_or_duplicated_within_its_mod_is_KEC0102()
    {
        ContentTypeRegistry registry = Registry();

        List<ContentFinding> outOfRange = Band(
            Snapshot(registry, Mod(registry, 1, "sharp"), ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 0)));
        Assert.Equal(1, Only(outOfRange, InstanceContentFindings.TierOrdinal).Id);

        List<ContentFinding> duplicated = Band(
            Snapshot(
                registry,
                Mod(registry, 1, "sharp"),
                ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 4),
                ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 4)));

        // The second row is the one reported, because the first is where the ordinal legitimately sits.
        Assert.Equal(2, Only(duplicated, InstanceContentFindings.TierOrdinal).Id);

        // The same ordinal on a DIFFERENT mod is ordinary: the payload stores the pair.
        List<ContentFinding> twoMods = Band(
            Snapshot(
                registry,
                Mod(registry, 1, "sharp"),
                Mod(registry, 2, "keen"),
                ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 4),
                ModTier(registry, 2, "keen_t1", modId: 2, ordinal: 4)));
        NoneOf(twoMods, InstanceContentFindings.TierOrdinal);
    }

    [Fact]
    public void A_mod_tier_ordinal_above_15_is_KEC0102_because_the_candidate_table_packs_four_bits()
    {
        ContentTypeRegistry registry = Registry();

        // Fifteen is the ceiling the packed key can hold and it publishes clean. Sixteen would alias onto
        // another tier of the same mod inside the candidate table, so the ceiling is loud at publish rather
        // than silent in a table.
        NoneOf(
            Band(Snapshot(
                registry,
                Mod(registry, 1, "sharp"),
                ModTier(registry, 1, "sharp_t15", modId: 1, ordinal: ModCandidateTables.MaxTierOrdinal))),
            InstanceContentFindings.TierOrdinal);

        List<ContentFinding> findings = Band(Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t16", modId: 1, ordinal: ModCandidateTables.MaxTierOrdinal + 1)));

        ContentFinding finding = Only(findings, InstanceContentFindings.TierOrdinal);
        Assert.Equal(InstanceContentTypeIds.ModTierTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
        Assert.Contains("16", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_item_base_whose_authored_tag_list_passes_the_generation_ceiling_is_KEC0114()
    {
        ContentTypeRegistry registry = Registry();
        var rows = new List<ContentRow>();
        var tags = new List<int>();
        for (int tag = 1; tag <= ModCandidateTables.MaxGenerationTagPositions + 1; tag++)
        {
            tags.Add(tag);
            rows.Add(Tag(registry, tag, FormattableString.Invariant($"tag{tag}")));
        }

        // At the ceiling it publishes, because the candidate tables hold one overlap header per tag POSITION
        // for every signature, band and kind, and the ceiling is what bounds that product.
        rows.Add(Item(registry, 40, "greatsword", tags.GetRange(0, ModCandidateTables.MaxGenerationTagPositions)));
        NoneOf(Band(Snapshot(registry, rows.ToArray())), InstanceContentFindings.GenerationTagPositions);

        rows[^1] = Item(registry, 40, "greatsword", tags);
        ContentFinding finding = Only(
            Band(Snapshot(registry, rows.ToArray())),
            InstanceContentFindings.GenerationTagPositions);
        Assert.Equal(EngineContentTypes.ItemTypeId, finding.Type.Value);
        Assert.Equal(40, finding.Id);
        Assert.Contains("9 authored tags", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_reorder_since_the_previous_published_version_is_KEC0103()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot previous = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 2));
        ContentSnapshot candidate = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 2),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 1));

        List<ContentFinding> findings = Band(candidate, previous);

        // Both rows moved, and the ordinal is in every stored payload, so both are reported. The SET of
        // ordinals is unchanged, so the removal half of the check stays quiet.
        Assert.Equal(2, findings.Count);
        foreach (ContentFinding finding in findings)
        {
            Assert.Equal(InstanceContentFindings.TierOrdinalMoved, finding.Code);
        }
    }

    [Fact]
    public void A_tier_ordinal_removed_since_the_previous_version_with_no_rule_covering_it_is_KEC0103()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot previous = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 2));
        ContentRow[] rows = [Mod(registry, 1, "sharp"), ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1)];

        List<ContentFinding> uncovered = Band(Snapshot(registry, rows), previous);
        ContentFinding finding = Only(uncovered, InstanceContentFindings.TierOrdinalMoved);
        Assert.Equal(2, finding.Id);

        // A rule naming the removed row is the remap the check asks for, so the same pair is clean. The
        // rules are the SET the sweep was handed rather than the ones the candidate happens to carry.
        RemapRule[] covering =
        [
            new RemapRule(1, 1, new ContentTypeId(InstanceContentTypeIds.ModTierTypeId), RemapRuleKind.Retired, 2, 0, [RemapRule.RetirePolicyPlaceholder]),
        ];
        NoneOf(Band(Snapshot(registry, rows), previous, covering), InstanceContentFindings.TierOrdinalMoved);
    }

    [Fact]
    public void A_tier_reorder_is_KEC0103_through_the_one_validator_with_a_previous()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot previous = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 2));
        ContentSnapshot candidate = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 2),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 1));

        // The one validator, with the previous version a publish holds, which is the only caller that has
        // one. Pass 6 hands it on, so the publish-only half of the band is reachable from a real publish.
        ContentValidationReport report = ContentValidator.Validate(candidate, previous, [], registry);

        Assert.Equal(2, CountOf(report, InstanceContentFindings.TierOrdinalMoved));
        Assert.False(report.IsValid, DescribeReport(report));
    }

    [Fact]
    public void A_rarity_rule_id_that_moved_is_KEC0111_through_the_one_validator_with_a_previous()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot previous = Snapshot(registry, RarityRule(registry, 5, "rare"));
        ContentSnapshot candidate = Snapshot(registry, RarityRule(registry, 6, "rare"));

        ContentValidationReport report = ContentValidator.Validate(candidate, previous, [], registry);

        ContentFinding finding = Single(report, InstanceContentFindings.RarityRuleIdMoved);
        Assert.Equal(InstanceContentTypeIds.RarityRuleTypeId, finding.Type.Value);
        Assert.Equal(6, finding.Id);
    }

    [Fact]
    public void A_stat_line_whose_combine_is_not_1_2_or_3_or_whose_min_exceeds_max_is_KEC0104()
    {
        ContentTypeRegistry registry = Registry();
        ContentRow mod = Mod(registry, 1, "sharp");
        ContentRow tier = ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1);
        ContentRow stat = Stat(registry, 3, "attack");

        List<ContentFinding> badCombine = Band(
            Snapshot(registry, stat, mod, tier, StatLine(registry, 1, "sharp_t1_l1", tierId: 1, statId: 3, combine: 4)));
        Assert.Equal(1, Only(badCombine, InstanceContentFindings.StatLineShape).Id);

        List<ContentFinding> badRange = Band(
            Snapshot(registry, stat, mod, tier, StatLine(registry, 1, "sharp_t1_l1", tierId: 1, statId: 3, min: 40, max: 10)));
        Assert.Equal(1, Only(badRange, InstanceContentFindings.StatLineShape).Id);

        List<ContentFinding> danglingStat = Band(
            Snapshot(registry, mod, tier, StatLine(registry, 1, "sharp_t1_l1", tierId: 1, statId: 9)));
        Assert.Equal(1, Only(danglingStat, InstanceContentFindings.StatLineShape).Id);
    }

    [Fact]
    public void A_mod_group_max_per_item_below_1_is_KEC0105()
    {
        ContentTypeRegistry registry = Registry();

        List<ContentFinding> findings = Band(Snapshot(registry, ModGroup(registry, 1, "added_attack", maxPerItem: 0)));

        ContentFinding finding = Only(findings, InstanceContentFindings.ModGroupCount);
        Assert.Equal(InstanceContentTypeIds.ModGroupTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
    }

    [Fact]
    public void A_rarity_rule_whose_upgrade_from_chain_cycles_is_KEC0106()
    {
        ContentTypeRegistry registry = Registry();

        List<ContentFinding> cycle = Band(
            Snapshot(
                registry,
                RarityRule(registry, 1, "magic", upgradeFrom: 2),
                RarityRule(registry, 2, "rare", upgradeFrom: 1)));

        // One finding per cycle, reported at its lowest id, so the code does not multiply with the length
        // of the chain.
        Assert.Equal(1, Only(cycle, InstanceContentFindings.RarityRuleShape).Id);

        List<ContentFinding> counts = Band(
            Snapshot(registry, RarityRule(registry, 1, "rare", minAffixes: 6, maxAffixes: 4, maxPrefixes: 3, maxSuffixes: 3)));
        Assert.Equal(1, Only(counts, InstanceContentFindings.RarityRuleShape).Id);

        List<ContentFinding> kinds = Band(
            Snapshot(registry, RarityRule(registry, 1, "rare", minAffixes: 1, maxAffixes: 6, maxPrefixes: 2, maxSuffixes: 2)));
        Assert.Equal(1, Only(kinds, InstanceContentFindings.RarityRuleShape).Id);

        // A forest with no cycle is the ordinary shape and says nothing.
        NoneOf(
            Band(Snapshot(registry, RarityRule(registry, 1, "magic"), RarityRule(registry, 2, "rare", upgradeFrom: 1))),
            InstanceContentFindings.RarityRuleShape);
    }

    [Theory]
    [InlineData("11")]
    [InlineData("1122")]
    [InlineData("11223344")]
    public void A_rarity_colour_with_the_wrong_byte_length_is_refused_before_publish(string hex)
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(registry,
            RarityRule(registry, 1, "rare", displayRgb: Convert.FromHexString(hex)));

        ContentFinding finding = Only(Band(candidate), InstanceContentFindings.RarityRuleShape);
        Assert.Contains("display_rgb", finding.Message, StringComparison.Ordinal);

        ContentValidationReport report = Validate(candidate, registry);
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, value =>
            value.Code == InstanceContentFindings.RarityRuleShape && value.Id == 1);
    }

    [Fact]
    public void A_unique_line_whose_mod_carries_a_mod_tier_weight_row_is_KEC0107()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Item(registry, 40, "greatsword"),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: 1000),
            UniqueTemplate(registry, 1, "sunbrand", baseId: 40),
            UniqueLine(registry, 1, "sunbrand_line_1", templateId: 1, modId: 1));

        List<ContentFinding> findings = Band(candidate);

        ContentFinding finding = Only(findings, InstanceContentFindings.UniqueLineShape);
        Assert.Equal(InstanceContentTypeIds.UniqueLineTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);

        // The ordinal naming a tier the mod does not carry is the same code.
        List<ContentFinding> badOrdinal = Band(
            Snapshot(
                registry,
                Item(registry, 40, "greatsword"),
                Mod(registry, 1, "sharp"),
                ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
                UniqueTemplate(registry, 1, "sunbrand", baseId: 40),
                UniqueLine(registry, 1, "sunbrand_line_1", templateId: 1, modId: 1, tierOrdinal: 7)));
        Assert.Equal(1, Only(badOrdinal, InstanceContentFindings.UniqueLineShape).Id);
    }

    [Fact]
    public void Two_unique_sockets_of_one_template_sharing_a_sort_is_KEC0115()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(registry, 40, "greatsword"),
            UniqueTemplate(registry, 1, "sunbrand", baseId: 40),
            UniqueSocket(registry, 1, "sunbrand_socket_1", templateId: 1, sort: 0),
            UniqueSocket(registry, 2, "sunbrand_socket_2", templateId: 1, sort: 0));

        List<ContentFinding> findings = Band(candidate);

        // The second row is the one reported, because the first is where the index legitimately sits.
        ContentFinding finding = Only(findings, InstanceContentFindings.UniqueSocketSort);
        Assert.Equal(InstanceContentTypeIds.UniqueSocketTypeId, finding.Type.Value);
        Assert.Equal(2, finding.Id);

        // Two templates each holding index 0 is the ordinary shape: the index is per template.
        NoneOf(
            Band(Snapshot(
                registry,
                Item(registry, 40, "greatsword"),
                UniqueTemplate(registry, 1, "sunbrand", baseId: 40),
                UniqueTemplate(registry, 2, "moonbrand", baseId: 40),
                UniqueSocket(registry, 1, "sunbrand_socket_1", templateId: 1, sort: 0),
                UniqueSocket(registry, 2, "moonbrand_socket_1", templateId: 2, sort: 0))),
            InstanceContentFindings.UniqueSocketSort);
    }

    [Fact]
    public void Two_rarity_kind_limits_claiming_one_kind_for_one_rarity_is_KEC0116()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            RarityRule(registry, 1, "rare"),
            RarityKindLimit(registry, 1, "rare_implicit", rarityId: 1, modKind: 3, maxCount: 1),
            RarityKindLimit(registry, 2, "rare_implicit_again", rarityId: 1, modKind: 3, maxCount: 2));

        List<ContentFinding> findings = Band(candidate);

        ContentFinding finding = Only(findings, InstanceContentFindings.RarityKindClaimed);
        Assert.Equal(InstanceContentTypeIds.RarityKindLimitTypeId, finding.Type.Value);
        Assert.Equal(2, finding.Id);

        // One rarity limiting two kinds, and two rarities limiting one kind, are both ordinary.
        NoneOf(
            Band(Snapshot(
                registry,
                RarityRule(registry, 1, "rare"),
                RarityRule(registry, 2, "mythic"),
                RarityKindLimit(registry, 1, "rare_implicit", rarityId: 1, modKind: 3),
                RarityKindLimit(registry, 2, "rare_corrupt", rarityId: 1, modKind: 4),
                RarityKindLimit(registry, 3, "mythic_implicit", rarityId: 2, modKind: 3))),
            InstanceContentFindings.RarityKindClaimed);
    }

    [Fact]
    public void A_socket_type_whose_accept_and_reject_tag_sets_overlap_is_KEC0108()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 6, "gem"),
            SocketType(registry, 1, "gem_socket"),
            SocketTagRule(registry, 1, "gem_socket_accepts_gem", socketTypeId: 1, tagId: 6),
            SocketTagRule(registry, 2, "gem_socket_rejects_gem", socketTypeId: 1, tagId: 6, rule: SocketTagRuleContentType.RuleReject, sort: 2));

        List<ContentFinding> findings = Band(candidate);

        ContentFinding finding = Only(findings, InstanceContentFindings.SocketTagOverlap);
        Assert.Equal(InstanceContentTypeIds.SocketTypeTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
    }

    [Fact]
    public void A_rarity_position_with_no_word_of_non_zero_weight_reachable_is_KEC0109()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            RarityRule(registry, 1, "rare", nameWordPositions: 2),
            RarityWeight(registry, 1, "rare_metal", rarityId: 1, tagId: 5, weight: 1200),
            RareNameWord(registry, 1, "gloom", position: 1),
            RareNameWord(registry, 2, "blade", position: 2),
            RareNameWordWeight(registry, 1, "gloom_metal", wordId: 1, tagId: 5, weight: 400),
            RareNameWordWeight(registry, 2, "blade_metal", wordId: 2, tagId: 5, weight: 0));

        List<ContentFinding> findings = Band(candidate);

        ContentFinding finding = Only(findings, InstanceContentFindings.RareNameCoverage);
        Assert.Equal(InstanceContentTypeIds.RarityRuleTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
        Assert.Contains("position 2", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rare_name_word_position_outside_1_to_255_is_KEC0109()
    {
        ContentTypeRegistry registry = Registry();

        // The codec bounds this field too, and the publish validates BEFORE it encodes anything, so without
        // the check an authored 0 sweeps clean and then throws at pack time with no finding naming the row.
        List<ContentFinding> below = Band(Snapshot(registry, RareNameWord(registry, 1, "gloom", position: 0)));
        ContentFinding finding = Only(below, InstanceContentFindings.RareNameCoverage);
        Assert.Equal(InstanceContentTypeIds.RareNameWordTypeId, finding.Type.Value);
        Assert.Equal(1, finding.Id);
        Assert.Contains("position 0", finding.Message, StringComparison.Ordinal);

        List<ContentFinding> above = Band(
            Snapshot(registry, RareNameWord(registry, 1, "gloom", position: RareNameWordContentType.MaxPosition + 1)));
        Assert.Equal(1, Only(above, InstanceContentFindings.RareNameCoverage).Id);

        // Both ends of the range publish clean, because the bound is inclusive on both.
        NoneOf(
            Band(Snapshot(
                registry,
                RareNameWord(registry, 1, "gloom", position: RareNameWordContentType.MinPosition),
                RareNameWord(registry, 2, "blade", position: RareNameWordContentType.MaxPosition))),
            InstanceContentFindings.RareNameCoverage);
    }

    [Fact]
    public void A_rarity_that_names_no_words_at_all_is_covered_vacuously()
    {
        ContentTypeRegistry registry = Registry();

        // name_word_positions of 0 keeps the base name, which is how a game with no rare names authors its
        // rarities, so there is nothing to cover and the expensive check never runs.
        List<ContentFinding> findings = Band(
            Snapshot(
                registry,
                Tag(registry, 5, "metal"),
                RarityRule(registry, 1, "rare"),
                RarityWeight(registry, 1, "rare_metal", rarityId: 1, tagId: 5, weight: 1200)));

        NoneOf(findings, InstanceContentFindings.RareNameCoverage);
    }

    [Fact]
    public void A_currency_above_max_steps_or_with_a_duplicate_sort_is_KEC0110()
    {
        ContentTypeRegistry registry = Registry();

        List<ContentFinding> tooMany = Band(
            Snapshot(
                registry,
                Currency(registry, 1, "whetstone", maxSteps: 1),
                Step(registry, 1, "whetstone_1", currencyId: 1, sort: 1),
                Step(registry, 2, "whetstone_2", currencyId: 1, sort: 2)));
        Assert.Equal(1, Only(tooMany, InstanceContentFindings.CurrencyStepSet).Id);

        List<ContentFinding> duplicateSort = Band(
            Snapshot(
                registry,
                Currency(registry, 1, "whetstone", maxSteps: 4),
                Step(registry, 1, "whetstone_1", currencyId: 1, sort: 1),
                Step(registry, 2, "whetstone_2", currencyId: 1, sort: 1)));
        Assert.Equal(2, Only(duplicateSort, InstanceContentFindings.CurrencyStepSet).Id);
    }

    [Fact]
    public void A_rarity_rule_id_that_moved_since_the_previous_version_is_KEC0111()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot previous = Snapshot(registry, RarityRule(registry, 5, "rare"));
        ContentSnapshot candidate = Snapshot(registry, RarityRule(registry, 6, "rare"));

        List<ContentFinding> findings = Band(candidate, previous);

        // Kind 130 stores the rarity id, so moving the key onto a new id repoints every stored item.
        ContentFinding finding = Only(findings, InstanceContentFindings.RarityRuleIdMoved);
        Assert.Equal(InstanceContentTypeIds.RarityRuleTypeId, finding.Type.Value);
        Assert.Equal(6, finding.Id);
    }

    [Fact]
    public void A_negative_or_overflowing_weight_on_any_of_the_three_weight_types_is_KEC0112()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: -1),
            RarityRule(registry, 1, "rare"),
            RarityWeight(registry, 1, "rare_metal", rarityId: 1, tagId: 5, weight: -5),
            RareNameWord(registry, 1, "gloom"),
            RareNameWordWeight(registry, 1, "gloom_metal", wordId: 1, tagId: 5, weight: -2));

        List<ContentFinding> findings = Band(candidate);

        // A negative weight makes a prefix array non-monotonic and unsearchable, which is the hazard one
        // level up from the loot_entry weight of 944.
        var types = new List<ushort>();
        foreach (ContentFinding finding in findings)
        {
            Assert.Equal(InstanceContentFindings.WeightBelowZero, finding.Code);
            types.Add(finding.Type.Value);
        }

        Assert.Equal(
            new ushort[]
            {
                InstanceContentTypeIds.ModTierWeightTypeId,
                InstanceContentTypeIds.RarityWeightTypeId,
                InstanceContentTypeIds.RareNameWordWeightTypeId,
            },
            types);
    }

    [Fact]
    public void A_weight_bucket_summing_past_int_MaxValue_is_KEC0113()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            RarityRule(registry, 1, "magic"),
            RarityRule(registry, 2, "rare"),
            RarityWeight(registry, 1, "magic_metal", rarityId: 1, tagId: 5, weight: 2_000_000_000),
            RarityWeight(registry, 2, "rare_metal", rarityId: 2, tagId: 5, weight: 2_000_000_000));

        List<ContentFinding> findings = Band(candidate);

        // The sum is accumulated in a long and compared, never a checked add, so the overflow is reported
        // rather than thrown and the whole sweep still runs to its end.
        ContentFinding finding = Only(findings, InstanceContentFindings.WeightBucketOverflow);
        Assert.Equal(InstanceContentTypeIds.RarityWeightTypeId, finding.Type.Value);
        Assert.Contains("4000000000", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_parent_and_tag_pair_on_any_of_the_three_weight_types_is_KEC0117()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Tag(registry, 5, "metal"),
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: 5, weight: 100),
            ModTierWeight(registry, 2, "sharp_t1_metal_again", tierId: 1, tagId: 5, weight: 900),
            RarityRule(registry, 1, "rare"),
            RarityWeight(registry, 1, "rare_metal", rarityId: 1, tagId: 5, weight: 500),
            RarityWeight(registry, 2, "rare_metal_again", rarityId: 1, tagId: 5, weight: 700),
            RareNameWord(registry, 1, "gloom"),
            RareNameWordWeight(registry, 1, "gloom_metal", wordId: 1, tagId: 5, weight: 400),
            RareNameWordWeight(registry, 2, "gloom_metal_again", wordId: 1, tagId: 5, weight: 600));

        List<ContentFinding> findings = Band(candidate);

        // The SECOND row of a pair is neither summed into the first nor an alternative to it: the table
        // build records it as a repeat the overlap suppresses and the fold keeps the first row, so 900 of
        // authored weight simply does not exist. That is silent in both halves, which is why it is a
        // publish finding, and the finding names the SECOND row because the first is the one that survives.
        var types = new List<ushort>();
        var ids = new List<int>();
        foreach (ContentFinding finding in findings)
        {
            Assert.Equal(InstanceContentFindings.WeightRowRepeated, finding.Code);
            types.Add(finding.Type.Value);
            ids.Add(finding.Id);
        }

        Assert.Equal(
            new ushort[]
            {
                InstanceContentTypeIds.ModTierWeightTypeId,
                InstanceContentTypeIds.RarityWeightTypeId,
                InstanceContentTypeIds.RareNameWordWeightTypeId,
            },
            types);
        Assert.Equal(new[] { 2, 2, 2 }, ids);
    }

    [Fact]
    public void The_three_publish_only_checks_are_SKIPPED_when_previous_is_null_and_named_by_KEC0000()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 2),
            RarityRule(registry, 6, "rare"));
        ContentSnapshot previous = Snapshot(
            registry,
            Mod(registry, 1, "sharp"),
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            RarityRule(registry, 5, "rare"));

        List<ContentFinding> atBoot = Band(candidate, previous: null);
        NoneOf(atBoot, InstanceContentFindings.TierOrdinalMoved);
        NoneOf(atBoot, InstanceContentFindings.RarityRuleIdMoved);

        List<ContentFinding> atPublish = Band(candidate, previous);
        Assert.NotEmpty(atPublish);

        // Through the WHOLE sweep, both halves. A boot carries the informational code and neither
        // publish-only finding, and a publish carries both of them and no informational code, which is what
        // makes KEC0000 a statement about the run rather than a label nobody can act on.
        ContentValidationReport atBootReport = ContentValidator.Validate(candidate, previous: null, [], registry);
        _ = Single(atBootReport, ContentValidator.InformationalCode);
        Assert.Equal(0, CountOf(atBootReport, InstanceContentFindings.TierOrdinalMoved));
        Assert.Equal(0, CountOf(atBootReport, InstanceContentFindings.RarityRuleIdMoved));

        ContentValidationReport atPublishReport = ContentValidator.Validate(candidate, previous, [], registry);
        Assert.Equal(1, CountOf(atPublishReport, InstanceContentFindings.TierOrdinalMoved));
        Assert.Equal(1, CountOf(atPublishReport, InstanceContentFindings.RarityRuleIdMoved));
        Assert.Equal(0, CountOf(atPublishReport, ContentValidator.InformationalCode));
    }

    [Fact]
    public void A_valid_authored_set_produces_no_finding_at_all()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(registry, Authored(registry));

        List<ContentFinding> findings = Band(candidate);
        Assert.Empty(findings);

        ContentValidationReport report = ContentValidator.Validate(candidate, previous: null, [], registry);
        Assert.True(report.IsValid, DescribeReport(report));
    }

    [Fact]
    public void An_EMPTY_row_set_for_all_eighteen_types_is_VALID_because_every_cross_check_is_vacuous()
    {
        // Spec 8.10's OSRS row. Zero mod rows, zero rarity rules, zero uniques, all eighteen types
        // registered, and nothing here divides by or indexes into a count of zero.
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(registry);

        Assert.Empty(Band(candidate));
        Assert.Empty(Band(candidate, previous: Snapshot(registry)));

        ContentValidationReport report = ContentValidator.Validate(candidate, previous: null, [], registry);
        Assert.True(report.IsValid, DescribeReport(report));
        Assert.Equal([ContentValidator.InformationalCode], Codes(report));
    }

    [Fact]
    public void A_real_KEC0100_finding_is_reachable_from_a_publish_through_pass_6()
    {
        ContentTypeRegistry registry = Registry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Currency(registry, 1, "whetstone"),
            Currency(registry, 2, "grindstone"),
            Step(registry, 1, "whetstone_1", currencyId: 1),
            Guard(registry, 1, "grindstone_g1", currencyId: 2, stepId: 1));

        ContentValidationReport report = ContentValidator.Validate(candidate, previous: null, [], registry);

        // The band's code arrives UNCHANGED rather than folded into KEC0040, which is what lets an operator
        // key a runbook on it.
        ContentFinding finding = Single(report, InstanceContentFindings.ParentUnresolved);
        Assert.Equal(InstanceContentTypeIds.CurrencyGuardTypeId, finding.Type.Value);
        Assert.False(report.IsValid, DescribeReport(report));
        Assert.DoesNotContain(report.Findings, one => string.Equals(one.Code, ContentValidator.TypeValidatorCode, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_code_the_band_emits_is_inside_the_reserved_KEC0100_to_KEC0199_range()
    {
        foreach (string code in InstanceContentFindings.All)
        {
            Assert.Equal(7, code.Length);
            Assert.StartsWith("KEC", code, StringComparison.Ordinal);
            int number = int.Parse(code.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(number, ContentValidator.InstanceBandFirstCode, ContentValidator.InstanceBandLastCode);
        }

        // A code is a stable token, so the list is pinned rather than counted.
        Assert.Equal(
            new[]
            {
                "KEC0100", "KEC0101", "KEC0102", "KEC0103", "KEC0104", "KEC0105", "KEC0106",
                "KEC0107", "KEC0108", "KEC0109", "KEC0110", "KEC0111", "KEC0112", "KEC0113",
                "KEC0114", "KEC0115", "KEC0116", "KEC0117",
            },
            InstanceContentFindings.All);
    }

    static int CountOf(ContentValidationReport report, string code)
    {
        int count = 0;
        foreach (ContentFinding finding in report.Findings)
        {
            if (string.Equals(finding.Code, code, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    static string[] Codes(ContentValidationReport report)
    {
        var codes = new List<string>(report.Findings.Count);
        foreach (ContentFinding finding in report.Findings)
        {
            codes.Add(finding.Code);
        }

        return codes.ToArray();
    }

    static string DescribeReport(ContentValidationReport report) => Describe(report.Findings);
}
