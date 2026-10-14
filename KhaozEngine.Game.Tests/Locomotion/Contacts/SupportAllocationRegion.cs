using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Protects support allocation counters with the shared checked measurement region.</summary>
internal sealed class SupportAllocationRegion : IDisposable
{
    readonly AllocationRegion region;

    SupportAllocationRegion(AllocationRegion region) => this.region = region;

    internal static SupportAllocationRegion Start(bool enabled = true, long totalSize = AllocationRegion.DefaultBudget,
        [CallerMemberName] string description = "") =>
        new(AllocationRegion.Start(description, totalSize, enabled));

    public void Dispose() => region.Dispose();
}
