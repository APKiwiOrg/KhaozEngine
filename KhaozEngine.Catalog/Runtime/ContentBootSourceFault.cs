using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace KhaozEngine.Catalog;

/// <summary>
/// What the boot does when a version SOURCE throws: the directory's pinned and active reads at step 2, and the
/// version record's hash read at step 3. These are the host's own providers, ordinarily a database, and
/// <see cref="ContentBoot.RunAsync"/> promises a <see cref="ContentBootResult"/> rather than an exception, so
/// a fault in one of them is a refusal like any other: <see cref="ContentBootRefusal.VersionSourceUnreadable"/>,
/// exit code 3, and one line naming the read and the fault.
/// <para>
/// The caller's own cancellation is NOT a fault. Anything a source throws while the boot's token is cancelled
/// propagates as an <see cref="OperationCanceledException"/>, because the caller asked to stop and is not
/// waiting for a line, and a driver may report a cancelled read as its own exception type rather than as a
/// cancellation. A cancellation thrown with the token NOT cancelled, such as a driver's own timeout, is a fault
/// and refuses.
/// </para>
/// </summary>
internal static class ContentBootSourceFault
{
    /// <summary>
    /// Returns when the exception is a fault the boot refuses on, and throws when the caller cancelled: the
    /// cancellation itself unchanged, and any other exception wrapped in an <see cref="OperationCanceledException"/>
    /// on the boot's token with the source's exception as its inner exception.
    /// </summary>
    /// <param name="exception">What the source threw.</param>
    /// <param name="cancellationToken">The boot's own token.</param>
    public static void ThrowIfCallerCancelled(Exception exception, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (exception is OperationCanceledException)
        {
            ExceptionDispatchInfo.Throw(exception);
        }

        throw new OperationCanceledException(
            "The content boot was cancelled while a version source was being read.",
            exception,
            cancellationToken);
    }

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
