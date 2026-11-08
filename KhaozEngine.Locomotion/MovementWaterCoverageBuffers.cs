using System;
using System.Buffers;

namespace KhaozEngine.Locomotion;

// Two independent bounded lanes: producer validation is atomic, so its private outputs
// cannot alias the boundary resolver's borrowed published outputs. The workspace owns both.
internal sealed class MovementWaterCoverageBuffers
{
    readonly Storage _producer = new(), _boundary = new();

    internal Loan Borrow(bool boundary) => (boundary ? _boundary : _producer).Borrow();

    internal sealed class Storage
    {
        MovementCoverageSpan[]? _spans;
        MovementDomainContact[]? _contacts;
        bool _busy;

        internal Loan Borrow()
        {
            if (_busy)
            {
                // Producer callbacks may reenter the same lease. Keep their storage isolated
                // without adding a refusal or retaining an unbounded number of cached buffers.
                if (MovementWaterBufferWork.Enabled) MovementWaterBufferWork.Rents += 2;
                var spans = ArrayPool<MovementCoverageSpan>.Shared.Rent(MovementQueryLease.MaxCoverageSpans);
                var contacts = ArrayPool<MovementDomainContact>.Shared.Rent(MovementQueryLease.MaxDomainContacts);
                contacts.AsSpan(0, MovementQueryLease.MaxDomainContacts).Clear();
                return new(null, spans, contacts);
            }
            _spans ??= new MovementCoverageSpan[MovementQueryLease.MaxCoverageSpans];
            _contacts ??= new MovementDomainContact[MovementQueryLease.MaxDomainContacts];
            _busy = true;
            return new(this, _spans, _contacts);
        }

        internal void Release() => _busy = false;
    }

    internal readonly struct Loan(Storage? owner, MovementCoverageSpan[] spans, MovementDomainContact[] contacts)
    {
        readonly int _thread = Environment.CurrentManagedThreadId;
        internal Span<MovementCoverageSpan> Spans => spans.AsSpan(0, MovementQueryLease.MaxCoverageSpans);
        internal Span<MovementDomainContact> Contacts => contacts.AsSpan(0, MovementQueryLease.MaxDomainContacts);

        internal void Return(int exposedContacts)
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Water coverage buffers belong to their borrowing thread.");
            // A producer can partially write and then refuse or throw, so its returned count
            // cannot delimit initialized storage. Inspect the exposed slice, clearing only
            // nondefault runs. A validated consumer can delimit its exact written prefix.
            int at = 0;
            while (at < exposedContacts)
            {
                if (contacts[at] == default) { at++; continue; }
                int first = at++;
                while (at < exposedContacts && contacts[at] != default) at++;
                contacts.AsSpan(first, at - first).Clear();
                if (MovementWaterBufferWork.Enabled) MovementWaterBufferWork.ClearedReferences += at - first;
            }
            if (owner is not null) owner.Release();
            else
            {
                ArrayPool<MovementCoverageSpan>.Shared.Return(spans);
                ArrayPool<MovementDomainContact>.Shared.Return(contacts);
            }
        }
    }
}
