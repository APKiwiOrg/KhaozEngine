using System;
using System.Threading;

namespace KhaozEngine.Catalog;

/// <summary>
/// What the boot does when a version SOURCE throws: the directory's pinned and active reads at step 2, and the
/// version record's hash read at step 3. These are the host's own providers, ordinarily a database, and
/// <see cref="ContentBoot.RunAsync"/> promises a <see cref="ContentBootResult"/> rather than an exception, so
/// a fault in one of them is a refusal like any other: <see cref="ContentBootRefusal.VersionSourceUnreadable"/>,
/// exit code 3, and one line naming the read and the fault.
/// <para>
/// The caller's own cancellation is NOT a fault. An <see cref="OperationCanceledException"/> thrown while the
/// boot's token is cancelled propagates, because the caller asked to stop and is not waiting for a line. One
/// thrown with the token NOT cancelled, such as a driver's own timeout, is a fault and refuses.
/// </para>
/// </summary>
internal static class ContentBootSourceFault
{
    /// <summary>True when the exception is a fault the boot refuses on, false for the caller's cancellation.</summary>
    /// <param name="exception">What the source threw.</param>
    /// <param name="cancellationToken">The boot's own token.</param>
    public static bool IsFault(Exception exception, CancellationToken cancellationToken)
        => !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    /// <summary>The refusal: which read, and the fault's type and message on one line.</summary>
    /// <param name="step">The boot step the read belongs to.</param>
    /// <param name="read">What was being read, as the operator's line names it.</param>
    /// <param name="fault">What the source threw.</param>
    public static ContentBootResult Refuse(int step, string read, Exception fault)
        => ContentBootResult.Refuse(
            ContentBootRefusal.VersionSourceUnreadable,
            step,
            FormattableString.Invariant(
                $"{ContentBoot.LinePrefix}{read} unreadable ({fault.GetType().Name}: {OneLine(fault.Message)})."));

    /// <summary>
    /// The message as one line without its closing full stop, so a driver's multi-line message cannot split
    /// the operator's line and a message that ends in a stop does not end in two.
    /// </summary>
    static string OneLine(string message)
        => message.ReplaceLineEndings(" ").Trim().TrimEnd('.');
}
