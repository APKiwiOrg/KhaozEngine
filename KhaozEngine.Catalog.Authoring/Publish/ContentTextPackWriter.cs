using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Step 9 of a text publish: every newly encoded KECT chunk FIRST, then the row chunks, the rule chunk and
/// both manifests through the commit's scoped internal row writer. Text objects therefore exist before any
/// manifest that names them, and every object goes in at a content address nothing references yet, so a
/// crash anywhere here leaves inert bytes and the old version.
/// <para>
/// It does not go through <see cref="ContentPublishCommit.WriteAsync"/>, whose guard refuses the row half
/// of a text plan precisely because that public writer puts no text. The scoped entry is the same
/// put-if-absent row writer without the guard, reached only after the text puts.
/// </para>
/// </summary>
internal static class ContentTextPackWriter
{
    /// <summary>Writes the plan's objects in order and reports what was actually put.</summary>
    /// <param name="store">The pack store.</param>
    /// <param name="plan">The complete text plan.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    public static async Task<ContentPackWrite> WriteAsync(
        IPackStore store,
        ContentTextPublishPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plan);

        int objects = 0;
        long bytes = 0;
        foreach (ContentTextChunkRecord chunk in plan.Chunks)
        {
            if (chunk.IsReused)
            {
                continue;
            }

            (int wrote, long put) = await ContentPublishCommit
                .PutIfAbsentAsync(store, chunk.Hash, chunk.StoredFile, cancellationToken).ConfigureAwait(false);
            objects += wrote;
            bytes += put;
        }

        ContentPackWrite rows = await ContentPublishCommit
            .WriteRowsAsync(store, plan.RowPlan, cancellationToken).ConfigureAwait(false);
        return new ContentPackWrite(objects + rows.ObjectsWritten, bytes + rows.BytesWritten);
    }
}
