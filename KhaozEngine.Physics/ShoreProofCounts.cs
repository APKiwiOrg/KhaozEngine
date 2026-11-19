#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.Collections.Generic;

namespace KhaozEngine.Physics;

// Count-pass record of each water boundary proof: what its coverage held, what the boundary derived and
// whether a capture-level certificate would have implied the same answer. It records decisions already
// made and feeds nothing back into them. Thread-local and off until a count pass starts a step.
internal static class ShoreProofCounts
{
    internal sealed class Sampling
    {
        public int Index { get; init; }
        public string Label { get; init; } = "other";
        public bool Prism { get; init; }
        public int Columns { get; init; }
        public int Levels { get; init; }
        public int Domains { get; init; }
        public double? MaxSurfaceLocal { get; init; }
        public bool LevelCertificate { get; init; }
    }

    internal sealed class Proof
    {
        public string Label { get; init; } = "other";
        public string Mode { get; init; } = "";
        public bool Stationary { get; init; }
        public int? Sampler { get; set; }
        public string Coverage { get; set; } = "unreached";
        public int Spans { get; set; }
        public int Contacts { get; set; }
        public int FreeSurfaceContacts { get; set; }
        public int ClosedContacts { get; set; }
        public int MixedLevelSpans { get; set; }
        public int TangentContacts { get; set; }
        public float Error { get; set; }
        public string Status { get; set; } = "";
        public int Candidates { get; set; }
        public int Normals { get; set; }
        public bool Blocked { get; set; }
        public bool Overlap { get; set; }
        public double? WadeMargin { get; set; }
        public bool FreeSurfaceCertificate { get; set; }
        public bool WadeCertificate { get; set; }
    }

    [ThreadStatic] static List<Sampling>? t_samplings;
    [ThreadStatic] static List<Proof>? t_proofs;
    [ThreadStatic] static Proof? t_open;
    [ThreadStatic] internal static bool Active;

    internal static void Start()
    {
        (t_samplings ??= new()).Clear();
        (t_proofs ??= new()).Clear();
        t_open = null;
        Active = true;
    }

    internal static (Sampling[] Samplings, Proof[] Proofs) Stop()
    {
        Active = false;
        if (t_open is not null) throw new InvalidOperationException("A water boundary proof is still open.");
        return (t_samplings!.ToArray(), t_proofs!.ToArray());
    }

    internal static void Open(string mode, bool stationary)
    {
        if (!Active) return;
        if (t_open is not null) throw new InvalidOperationException("Water boundary proofs do not nest.");
        t_open = new Proof { Label = ShoreStepProbe.TraceLabel ?? "other", Mode = mode, Stationary = stationary };
    }

    internal static void Sampler(bool prism, int columns, int levels, int domains, double? maxSurfaceLocal,
        bool levelCertificate)
    {
        if (!Active) return;
        var sampling = new Sampling
        {
            Index = t_samplings!.Count, Label = ShoreStepProbe.TraceLabel ?? "other", Prism = prism,
            Columns = columns, Levels = levels, Domains = domains, MaxSurfaceLocal = maxSurfaceLocal,
            LevelCertificate = levelCertificate
        };
        t_samplings.Add(sampling);
        if (t_open is { Sampler: null } open) open.Sampler = sampling.Index;
    }

    internal static Sampling? OpenSampling() =>
        Active && t_open is { Sampler: int index } ? t_samplings![index] : null;

    internal static void Coverage(string availability, int spans, int contacts, int free, int closed,
        int mixedSpans, int tangent, float error)
    {
        if (!Active || t_open is not { } open) return;
        (open.Coverage, open.Spans, open.Contacts, open.FreeSurfaceContacts, open.ClosedContacts,
            open.MixedLevelSpans, open.TangentContacts, open.Error) =
            (availability, spans, contacts, free, closed, mixedSpans, tangent, error);
    }

    internal static void Close(string status, int candidates, int normals, bool blocked, bool overlap,
        double? wadeMargin, bool freeSurfaceCertificate, bool wadeCertificate)
    {
        if (!Active || t_open is not { } open) return;
        (open.Status, open.Candidates, open.Normals, open.Blocked, open.Overlap, open.WadeMargin,
            open.FreeSurfaceCertificate, open.WadeCertificate) =
            (status, candidates, normals, blocked, overlap, wadeMargin, freeSurfaceCertificate, wadeCertificate);
        t_proofs!.Add(open);
        t_open = null;
    }
}
#endif
