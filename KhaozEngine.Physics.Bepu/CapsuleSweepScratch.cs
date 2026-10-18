namespace KhaozEngine.Physics.Bepu;

/// <summary>One inactive-or-loaned query workspace. No topology is copied into it.</summary>
internal sealed class CapsuleSweepScratch
{
    internal SweepExactWorkspace Exact { get; } = new();
    internal SweepLinear[] Eligibility { get; } = new SweepLinear[16];
    internal InstalledSweepLeaf[] Pending { get; } = new InstalledSweepLeaf[CapsuleGeneralSweepPolicy.MaximumChildren];
}
