namespace KhaozEngine.Dungeon;

/// <summary>Rules <see cref="DungeonJson.LoadConfig"/> and <see cref="DungeonJson.LoadLayout"/> refuse, carried as
/// <see cref="DungeonJsonException.Reason"/> with the identifiers each member names.</summary>
public enum DungeonJsonRefusal
{
    // Document decoding, for a config or a layout.

    /// <summary>Refuses text that is not well-formed JSON (JSONC comments and trailing commas are accepted).</summary>
    JsonMalformed,

    /// <summary>Refuses a document whose root is not a JSON object.</summary>
    RootObjectRequired,

    /// <summary>Refuses a member whose JSON value cannot be converted to the member's type.</summary>
    JsonMemberUnconvertible,

    // Config values, in the order DungeonConfig.Validate checks them.

    /// <summary>Refuses a size that is not greater than zero.</summary>
    PositiveValueRequired,

    /// <summary>Refuses an integer below its fixed minimum.</summary>
    IntegerBelowMinimum,

    /// <summary>Refuses a minimum that exceeds the maximum it pairs with.</summary>
    RangeUnordered,

    /// <summary>Refuses a count or budget that is negative.</summary>
    NonnegativeValueRequired,

    /// <summary>Refuses a percentage outside 0 to 100 inclusive.</summary>
    PercentageOutsideRange,

    /// <summary>Refuses a plot dimension too small to fit the largest room plus a one-tile margin on each
    /// side.</summary>
    RoomMarginInsufficient,

    /// <summary>Refuses a plot dimension too small to fit the largest hall plus a one-tile margin on each side, when
    /// halls are enabled.</summary>
    HallMarginInsufficient,
}
