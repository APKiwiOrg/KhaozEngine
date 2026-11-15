namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    internal MovementWaterCoverageBuffers.Loan BorrowWaterCoverage(bool boundary)
    {
        AssertUsable();
        // Boundary output comes only from this lease's validated, atomic complete-contact copy.
        // Direct consumer pins keep the original cleanup contract for arbitrary partial writes.
        return Workspace.WaterCoverage.Borrow(boundary,
            completeWriter: boundary || _pin is IMovementCompleteWaterContactWriter);
    }
}
