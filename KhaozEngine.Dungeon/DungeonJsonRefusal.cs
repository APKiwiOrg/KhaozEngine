namespace KhaozEngine.Dungeon;

/// <summary>Rules <see cref="DungeonJson.LoadConfig"/> and <see cref="DungeonJson.LoadLayout"/> refuse, carried as
/// <see cref="DungeonJsonException.Reason"/> with the identifiers each member names.</summary>
public enum DungeonJsonRefusal
{
    // Document decoding, for a config or a layout.

    /// <summary>Refuses text that is not well-formed JSON, or that nests deeper than the reader's 64 levels (JSONC
    /// comments and trailing commas are accepted).</summary>
    JsonMalformed,

    /// <summary>Refuses a document whose root is not a JSON object.</summary>
    RootObjectRequired,

    /// <summary>Refuses a member whose JSON value cannot be converted to the member's type.</summary>
    JsonMemberUnconvertible,

    // Config values, in the order DungeonConfig.Validate checks them.

    /// <summary>Refuses a length that is not greater than zero.</summary>
    PositiveValueRequired,

    /// <summary>Refuses an integer below its fixed minimum, which is 1 except 0 for a count or budget.</summary>
    IntegerBelowMinimum,

    /// <summary>Refuses a minimum that exceeds the maximum it pairs with.</summary>
    RangeUnordered,

    /// <summary>Refuses a length that is negative.</summary>
    NonnegativeValueRequired,

    /// <summary>Refuses a percentage outside 0 to 100 inclusive.</summary>
    PercentageOutsideRange,

    /// <summary>Refuses a plot dimension too small to fit the largest room plus a one-tile margin on each
    /// side.</summary>
    RoomMarginInsufficient,

    /// <summary>Refuses a plot dimension too small to fit the largest hall plus a one-tile margin on each side, when
    /// halls are enabled.</summary>
    HallMarginInsufficient,

    // Layout content, in the order LoadLayout checks it. Layout lengths reuse PositiveValueRequired, layout
    // width, depth and floors reuse IntegerBelowMinimum and a negative ceiling height reuses
    // NonnegativeValueRequired.

    /// <summary>Refuses a layout ceiling height that is positive infinity. A negative value, negative infinity
    /// included, is <see cref="NonnegativeValueRequired"/>.</summary>
    NumberNonfinite,

    /// <summary>Refuses a JSON null for a layout section, an entry in one of its arrays or a list member of an entry.
    /// An absent section loads as empty.</summary>
    NullValue,

    /// <summary>Refuses a grid whose floor array count differs from the layout's <c>floors</c>.</summary>
    GridFloorCountMismatch,

    /// <summary>Refuses a grid floor whose row string count differs from the layout's <c>depth</c>.</summary>
    GridRowCountMismatch,

    /// <summary>Refuses a grid row string whose length differs from the layout's <c>width</c>.</summary>
    GridColumnCountMismatch,

    /// <summary>Refuses a room whose id an earlier room already uses.</summary>
    RoomIdDuplicate,

    /// <summary>Refuses an edge or key that names a room id no room carries.</summary>
    ReferencedRoomMissing,

    /// <summary>Refuses a key whose lock id no edge carries as its lock.</summary>
    ReferencedLockMissing,

    /// <summary>Refuses a grid character that names no cell kind.</summary>
    GridCharacterUnknown,
}
