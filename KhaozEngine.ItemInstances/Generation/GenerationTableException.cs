using System;

namespace KhaozEngine.ItemInstances;

/// <summary>A refused boot-time generation table build. Existing <see cref="InvalidOperationException"/>
/// catches remain valid. The reason and identifiers describe the refusal. The message is developer prose.</summary>
public sealed class GenerationTableException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    public GenerationTableException(GenerationTableRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused table construction.</summary>
    public GenerationTableRefusal Reason { get; }
    /// <summary>The item base whose authored tag list exceeds its limit.</summary>
    public int? BaseId { get; init; }
    /// <summary>The tag whose candidate bucket exceeds its entry or weight limit.</summary>
    public int? TagId { get; init; }
    /// <summary>The mod kind of the refused bucket or live pool.</summary>
    public int? ModKind { get; init; }
    /// <summary>The zero-based level band of the refused bucket or live pool.</summary>
    public int? Band { get; init; }
    /// <summary>The refused tag-position, bucket-entry or worst-case excluded-run count.</summary>
    public long? ActualCount { get; init; }
    /// <summary>The inclusive count limit for the rule. Boot limits are 8 tag positions, 65,535 bucket
    /// entries and 4,096 excluded runs. A direct signature build carries its supplied tag-position limit.</summary>
    public int? MaximumCount { get; init; }
    /// <summary>The refused cumulative bucket weight or live union weight.</summary>
    public long? ActualWeight { get; init; }
    /// <summary>The inclusive weight ceiling, 2,147,483,647.</summary>
    public int? MaximumWeight { get; init; }
    /// <summary>The widest rarity's affix count, with the run calculation's floor of one applied.</summary>
    public long? AffixCount { get; init; }
    /// <summary>The widest mod group represented in the candidate buckets, with a floor of one.</summary>
    public int? WidestGroup { get; init; }
    /// <summary>The widest candidate bucket's entry count, with a floor of one.</summary>
    public int? WidestBucket { get; init; }
    /// <summary>The mod id that does not fit the packed key.</summary>
    public int? ModId { get; init; }
    /// <summary>The inclusive packed mod id ceiling, 134,217,727.</summary>
    public int? MaximumModId { get; init; }
    /// <summary>The zero-based tag signature whose live pool or rarity name coverage was refused.</summary>
    public int? Signature { get; init; }
    /// <summary>The reachable rarity whose name position has no word of positive folded weight.</summary>
    public int? RarityId { get; init; }
    /// <summary>The one-based uncovered name position.</summary>
    public int? Position { get; init; }
    /// <summary>The number of name positions the refused rarity rolls.</summary>
    public int? RequiredPositions { get; init; }
    /// <summary>The mod tier whose ordinal exceeds the packed key's ceiling.</summary>
    public int? TierId { get; init; }
    /// <summary>The refused tier ordinal, before any narrowing conversion.</summary>
    public long? ActualOrdinal { get; init; }
    /// <summary>The inclusive packed tier ordinal ceiling, 15.</summary>
    public int? MaximumOrdinal { get; init; }
}
