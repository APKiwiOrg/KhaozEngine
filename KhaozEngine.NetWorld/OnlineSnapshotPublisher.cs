using System;
using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The end-of-tick publisher behind <see cref="IAdminControllable.ListOnline"/>, shared by both servers. It keeps
/// the rebuild buffer alive across ticks and hands <see cref="AdminCommandBuffer.Publish"/> a fresh array only when
/// the online view actually changed.
/// </summary>
/// <remarks>
/// <para>The published list has to be immutable once published: <see cref="AdminCommandBuffer.Online"/> is read
/// lock-free from another thread (the admin HTTP endpoint serializes it), so a reused buffer handed out directly
/// would be rewritten under a reader mid-enumeration. That is why the rebuild buffer is never published: it is
/// compared against the last published array and copied only when they differ.</para>
/// <para>The upshot for the serve loop is that a tick where nothing an admin can see moved allocates nothing at
/// all, and a tick that did change allocates one array instead of the old list-plus-<c>ToArray</c> pair (#134).
/// What a reader observes is unchanged: the published content is byte-for-byte what an unconditional rebuild
/// would have produced, so the "at most one tick stale" contract still holds exactly.</para>
/// </remarks>
internal sealed class OnlineSnapshotPublisher
{
    private readonly List<OnlinePlayer> scratch = new();
    private OnlinePlayer[] published = Array.Empty<OnlinePlayer>();

    /// <summary>Returns the cleared, capacity-retaining buffer for the caller to fill with this tick's view.</summary>
    public List<OnlinePlayer> BeginRebuild()
    {
        scratch.Clear();
        return scratch;
    }

    /// <summary>
    /// Publishes what <see cref="BeginRebuild"/>'s buffer now holds, as a fresh immutable array, unless it is
    /// element-for-element what is already published. Returns whether a new array was published (tests).
    /// </summary>
    public bool PublishIfChanged(AdminCommandBuffer admin)
    {
        if (Matches(scratch, published)) return false;
        published = scratch.ToArray();
        admin.Publish(published);
        return true;
    }

    private static bool Matches(List<OnlinePlayer> built, OnlinePlayer[] current)
    {
        if (built.Count != current.Length) return false;
        for (int i = 0; i < current.Length; i++)
        {
            if (!built[i].Equals(current[i])) return false;
        }
        return true;
    }
}
