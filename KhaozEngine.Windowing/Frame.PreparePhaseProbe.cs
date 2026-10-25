using System;

namespace KhaozEngine.Windowing;

public sealed partial class Frame
{
    /// <summary>True after this frame owner has successfully submitted a private scene prefix.</summary>
    public bool PreparePhaseSplitActive => PreparePhaseSubmissionCount > 0;
    /// <summary>Cumulative successful prefix submissions in this window, for the private comparison recorder.</summary>
    public long PreparePhaseSubmissionCount { get; private set; }
    /// <summary>Cumulative matching final composites recorded in this window.</summary>
    public long PreparePhaseCompositeCount { get; private set; }

    /// <summary>Private probe producer notification. Called only after the prefix submission returns.</summary>
    public void RecordPreparePhaseSubmission() => PreparePhaseSubmissionCount++;

    /// <summary>Private probe producer notification. Called only after the final scene output records successfully.</summary>
    public void RecordPreparePhaseComposite()
    {
        if (PreparePhaseCompositeCount >= PreparePhaseSubmissionCount)
            throw new InvalidOperationException("A prepare-phase composite requires a submitted prefix.");
        PreparePhaseCompositeCount++;
    }
}
