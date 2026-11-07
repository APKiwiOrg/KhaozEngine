namespace KhaozEngine.Render3D;

/// <summary>The stable reasons a moment schedule table is refused.</summary>
public enum MomentScheduleRefusal
{
    /// <summary>Refuses a non-finite or non-positive slot duration.</summary>
    SlotSecondsInvalid,

    /// <summary>Refuses a non-finite or negative quiet duration.</summary>
    QuietSecondsInvalid,

    /// <summary>Refuses an empty share outside the finite half-open unit interval.</summary>
    EmptyShareInvalid,

    /// <summary>Refuses a negative empty-run limit.</summary>
    MaxEmptySlotsInvalid,

    /// <summary>Refuses a schedule with no moment families.</summary>
    FamiliesEmpty,

    /// <summary>Refuses a non-finite or non-positive family weight.</summary>
    FamilyWeightInvalid,

    /// <summary>Refuses a non-finite or non-positive family duration.</summary>
    FamilyLengthInvalid,

    /// <summary>Refuses a family longer than the space between quiet ends.</summary>
    FamilyDoesNotFitSlot,

    /// <summary>Refuses a non-finite sum of family weights.</summary>
    WeightTotalNotFinite,
}
