using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

public sealed partial class NetServer
{
    // Slots a transport disconnect left allocated because the host asked to hold them. The slot keeps its allocator
    // bit and holds no connection. A held slot with an empty subject (a tokenless guest) is in heldSubjectBySlot only,
    // because nothing can reclaim it. The two move together, so heldSlotBySubject[s] always points back at a slot whose
    // heldSubjectBySlot entry is s.
    private readonly Dictionary<string, int> heldSlotBySubject = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> heldSubjectBySlot = new();

    /// <summary>Asked once with the slot when the transport reports an established slot disconnected (the client
    /// closed or timed out). True keeps the slot allocated for the subject that held it: <c>Left</c> is still enqueued
    /// as a terminal event and the connection is gone, but no other join takes the slot, and a later Hello from the
    /// same non-empty subject is seated back on it ahead of the duplicate session check and the capacity check. Free it
    /// with <see cref="ReleaseHeldSlot"/>. Null, the default, or false frees the slot as before.
    /// <para>Never asked for a session this server ends itself (a duplicate session kick), a refused Hello or a
    /// connection that never joined. A kick through <see cref="Disconnect(int)"/> does surface as a transport
    /// disconnect, so a host that must not hold a kicked slot answers false for it.</para>
    /// <para>Runs inside <see cref="Poll"/>. It must be cheap and must not throw.</para></summary>
    public Func<int, bool>? HoldSlotOnDisconnect { get; set; }

    /// <summary>Frees a slot <see cref="HoldSlotOnDisconnect"/> held and forgets its subject, so the subject's next
    /// Hello is an ordinary join. No <c>Left</c> is enqueued, the hold's disconnect already enqueued one. No-op for a
    /// slot that is not held.</summary>
    public void ReleaseHeldSlot(int slot)
    {
        if (!heldSubjectBySlot.Remove(slot, out string? subject)) return;
        if (subject.Length > 0) heldSlotBySubject.Remove(subject);
        slots.Release(slot);
    }

    // A transport disconnect of an established slot. Detaches the connection and either frees the slot or, when the
    // host holds it, keeps the allocator bit and moves the subject to the held table.
    private void DetachDisconnected(NetConnectionId conn, int slot)
    {
        bool hold = HoldSlotOnDisconnect?.Invoke(slot) ?? false;
        if (!hold)
        {
            RemovePeer(conn, slot, holdSlot: false);
            return;
        }
        string subject = subjectBySlot.TryGetValue(slot, out string? s) ? s : string.Empty;
        RemovePeer(conn, slot, holdSlot: true);
        heldSubjectBySlot[slot] = subject;
        if (subject.Length > 0) heldSlotBySubject[subject] = slot;
    }

    // Takes a held slot back for its returning subject. The allocator bit was never released, so the slot is seated
    // without an allocation.
    private bool TryReclaimHeldSlot(string subject, out int slot)
    {
        if (string.IsNullOrEmpty(subject) || !heldSlotBySubject.Remove(subject, out slot))
        {
            slot = -1;
            return false;
        }
        heldSubjectBySlot.Remove(slot);
        return true;
    }
}
