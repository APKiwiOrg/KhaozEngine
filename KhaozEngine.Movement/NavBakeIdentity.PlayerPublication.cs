using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

internal static partial class NavBakeIdentity
{
    internal const string PlayerPublicationSource = "engine/player-publication";

    // An engine-owned source row binds the policy without changing any frozen identity or payload layout.
    // Legacy-only files keep their original identity rules. Every Contact profile, including disabled Drops,
    // refuses an earlier policy even when a consumer explicitly supplies the same engine version.
    static IReadOnlyList<(string Label, byte[] Digest)> BindPlayerPublication(
        IReadOnlyList<(string Label, byte[] Digest)> sources, NavBakeProfile[] profiles)
    {
        if (!Array.Exists(profiles, static p => p.Tuning.Controller == MovementControllerKind.Contact)) return sources;
        var bound = new (string Label, byte[] Digest)[sources.Count + 1];
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Label == PlayerPublicationSource)
                throw new ArgumentException("The player publication source label is engine-owned.", nameof(sources));
            bound[i] = sources[i];
        }
        bound[^1] = (PlayerPublicationSource, SHA256.HashData(Encoding.UTF8.GetBytes(PlayerMovementPublication.Identity)));
        Array.Sort(bound, static (a, b) => string.CompareOrdinal(a.Label, b.Label));
        return bound;
    }
}
