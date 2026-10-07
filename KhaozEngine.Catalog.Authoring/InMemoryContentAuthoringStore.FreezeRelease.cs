using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>The atomic release that leaves a later draft's freeze intact.</summary>
public sealed partial class InMemoryContentAuthoringStore : IContentDraftFreezeStore
{
    /// <inheritdoc />
    Task IContentDraftFreezeStore.ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBaseVersion);
        lock (_gate)
        {
            if (_draft?.FrozenForBaseVersion == expectedBaseVersion)
            {
                ClearFreeze();
            }

            return Task.CompletedTask;
        }
    }
}
