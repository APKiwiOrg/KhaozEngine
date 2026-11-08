using System;

namespace KhaozEngine.Dungeon;

/// <summary>The first rule a <see cref="DungeonConfig"/> breaks, typed. <see cref="DungeonConfig.Validate"/> throws it
/// as the plain <see cref="ArgumentException"/> its callers catch, and <see cref="DungeonJson.LoadConfig"/> throws it
/// as a <see cref="DungeonJsonException"/> carrying the same reason and values.</summary>
internal sealed class DungeonConfigFinding
{
    internal DungeonConfigFinding(DungeonJsonRefusal reason, string propertyName, string message)
    {
        Reason = reason;
        PropertyName = propertyName;
        Message = message;
    }

    /// <summary>The rule the config breaks.</summary>
    public DungeonJsonRefusal Reason { get; }

    /// <summary>The CLR property the rule names, the parameter name <see cref="DungeonConfig.Validate"/> reports.</summary>
    public string PropertyName { get; }

    /// <summary>The explanation, without the parameter suffix <see cref="ArgumentException"/> appends.</summary>
    public string Message { get; }

    /// <summary>The refused size.</summary>
    public double? ActualNumber { get; init; }

    /// <summary>The refused integer.</summary>
    public long? ActualInteger { get; init; }

    /// <summary>The inclusive minimum the integer is held to.</summary>
    public long? MinimumInteger { get; init; }

    /// <summary>The inclusive maximum the integer is held to.</summary>
    public long? MaximumInteger { get; init; }

    /// <summary>The minimum of an unordered pair.</summary>
    public long? ActualMinimum { get; init; }

    /// <summary>The maximum of an unordered pair.</summary>
    public long? ActualMaximum { get; init; }

    /// <summary>The exception <see cref="DungeonConfig.Validate"/> throws for this finding.</summary>
    public ArgumentException ToArgumentException() => new(Message, PropertyName);
}
