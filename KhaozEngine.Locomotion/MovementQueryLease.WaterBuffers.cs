namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    internal MovementWaterCoverageBuffers.Loan BorrowWaterCoverage(bool boundary)
    {
        AssertUsable();
        return Workspace.WaterCoverage.Borrow(boundary);
    }
}
