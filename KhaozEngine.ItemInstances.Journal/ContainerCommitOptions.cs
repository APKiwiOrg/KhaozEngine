using KhaozEngine.ItemInstances;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// The journal facts a batch needs that are not operations: which version of the stream it builds on, what
/// its sections and its result are filed as, and the limits it is bounded by. They sit here rather than on
/// <c>Open</c> because every one of them has a right answer a caller usually takes.
/// </summary>
public sealed record ContainerCommitOptions
{
    /// <summary>The projection schema a container page is stored under.</summary>
    public const string DefaultProjectionSchema = "item-container";

    /// <summary>The result schema a container batch answers with.</summary>
    public const string DefaultResultSchema = "item-container.result";

    /// <summary>The stream version the batch builds on, which is the ADMITTED head rather than the committed
    /// one whenever something is already queued (<c>JournalAdmittedStream.AdmittedHeadVersion</c>).</summary>
    public long ExpectedVersion { get; init; }

    /// <summary>The projection schema every page write is filed under.</summary>
    public string ProjectionSchema { get; init; } = DefaultProjectionSchema;

    /// <summary>The projection schema version, which is the page codec's own.</summary>
    public int ProjectionSchemaVersion { get; init; } = ItemContainerPageCodec.Version;

    /// <summary>The result schema the commit answers with.</summary>
    public string ResultSchema { get; init; } = DefaultResultSchema;

    /// <summary>The result schema version.</summary>
    public int ResultSchemaVersion { get; init; } = 1;

    /// <summary>The limits the batch is bounded by. The engine maxima by default, which is where spec 6.4's
    /// 128 events, 64 projection writes and 8 MiB come from.</summary>
    public JournalLimits Limits { get; init; } = JournalLimits.Maximum;

    /// <summary>Whether the commit queues behind what is already admitted on its stream, which is the
    /// journal's own default and what makes a click land behind a held craft rather than being refused.</summary>
    public bool QueueBehindAdmitted { get; init; } = true;
}
