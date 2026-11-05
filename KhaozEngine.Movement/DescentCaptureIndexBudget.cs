using System;

namespace KhaozEngine.Movement;

/// <summary>Aggregate numeric storage and conservative bookkeeping reserve, limited by array representability.
/// Existing capture feature/reference budgets remain independent and unchanged.</summary>
internal sealed class DescentCaptureIndexBudget
{
    readonly long _maximumBytes;
    internal DescentCaptureIndexBudget() : this(Array.MaxLength) { }
    internal DescentCaptureIndexBudget(long maximumBytes) => _maximumBytes = maximumBytes;
    internal long UsedBytes { get; private set; }
    internal void Reserve(long count, int bytesPerRecord)
    {
        long required;
        try { required = checked(UsedBytes + checked(count * bytesPerRecord)); }
        catch (OverflowException) { throw new PhysicsNavDescentEvidenceException(long.MaxValue, _maximumBytes); }
        if (count < 0 || bytesPerRecord < 0 || required > _maximumBytes)
            throw new PhysicsNavDescentEvidenceException(required, _maximumBytes);
        UsedBytes = required;
    }
}
