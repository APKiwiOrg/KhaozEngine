using System;

namespace KhaozEngine.Physics.Bepu;

// Internal, opt-in structural diagnostics. No clocks or public counter contract.
internal sealed class SupportProposalAdmissionCapture : IDisposable
{
    [ThreadStatic] static SupportProposalAdmissionCapture? t_current;
    readonly SupportProposalAdmissionCapture? _prior = t_current;
    internal long Hits { get; private set; }
    internal long Misses { get; private set; }
    internal long Bypasses { get; private set; }

    internal SupportProposalAdmissionCapture() => t_current = this;

    internal static void Record(bool? hit)
    {
        if (t_current is not { } capture) return;
        if (hit == true) capture.Hits++;
        else if (hit == false) capture.Misses++;
        else capture.Bypasses++;
    }

    public void Dispose() => t_current = _prior;
}
