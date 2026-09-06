using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore.Journal;

public interface IMutationJournalMaintenance
{
    Task<JournalOperationPurgeResult> PurgeOperationsAsync(JournalOperationPurge purge, CancellationToken cancellationToken = default);
    Task<Guid> RotateStoreEpochAsync(CancellationToken cancellationToken = default);
}
