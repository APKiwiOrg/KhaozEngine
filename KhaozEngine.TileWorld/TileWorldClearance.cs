using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.TileWorld;

/// <summary>
/// The authoring-time clearance check: places every loaded object's mesh exactly as the prop renderer draws it
/// (<see cref="TileObjectPlacement.LocalToWorld"/>) and reports each subject that sits inside an obstacle deeper
/// than its rule allows. The document's <see cref="TileWorldDocument.Clearance"/> declares the rules, and
/// <see cref="ITileMeshGeometry"/> supplies the meshes. <see cref="TileWorldValidator.Validate"/> never runs it,
/// because it needs mesh files the validator cannot reach. The tools run it beside the validator.
/// <para>Codes: <c>clearance.rule</c> when a rule's subject or obstacle tags match no catalog archetype, so a typo
/// cannot pass everything, <c>clearance.mesh</c> once per archetype whose mesh cannot be read or built, so its
/// objects are never silently passed, and <c>clearance.overlap</c> once per penetrating pair, at its deepest point.
/// Issues come back rule issues first, then mesh issues by archetype id, then overlaps by rule order, subject id
/// and obstacle id.</para>
/// <para>Cost stays near linear in object count: each mesh is loaded, rasterized and sampled once per call, and a
/// coarse planar bucket grid with placed bounds limits the pairs that are probed to the ones that can touch.</para>
/// </summary>
public static class TileWorldClearance
{
    public const string OverlapCode = "clearance.overlap", MeshCode = "clearance.mesh", RuleCode = "clearance.rule";

    /// <summary>Edge of one broad-phase bucket, in world metres. Several buildings wide, so most objects land in one
    /// bucket and a bucket holds a street's worth of obstacles rather than the whole world.</summary>
    const float BucketMetres = 16f;

    /// <summary>The most buckets one object is spread over. A wider object is tested against every object on the
    /// other side instead, so a pathological extent can never allocate or walk a huge bucket range.</summary>
    const long MaxBucketSpan = 1024;

    /// <summary>The absolute mesh directory: <see cref="TileClearanceSettings.MeshRoot"/> resolved against the
    /// directory holding <c>world.json</c>.</summary>
    public static string ResolveMeshRoot(string worldDirectory, TileClearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(worldDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        return Path.GetFullPath(Path.Combine(worldDirectory, settings.MeshRoot));
    }

    /// <summary>Every clearance issue in <paramref name="doc"/>'s loaded regions. Empty when the document declares
    /// no clearance settings or no rules. An object whose archetype the catalogs lack is skipped, because the
    /// validator already reports it as <c>archetype.missing</c>. An object is never paired with itself, and a pair
    /// is skipped when either object carries the settings' waiver tag. Pairs across planes are checked.</summary>
    public static IReadOnlyList<TileWorldIssue> Check(TileWorldDocument doc, TileWorldCatalogs catalogs,
        ITileMeshGeometry meshes)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(meshes);
        if (doc.Clearance is not { Rules.Count: > 0 } settings) return Array.Empty<TileWorldIssue>();

        var issues = new List<TileWorldIssue>();
        var pieces = new Dictionary<string, Piece>(StringComparer.Ordinal);
        List<Placed> placed = PlacedObjects(doc, catalogs, settings.WaiverTag, pieces);

        var plans = new List<RulePlan>(settings.Rules.Count);
        foreach (TileClearanceRule rule in settings.Rules)
        {
            bool subjectsExist = AnyArchetype(catalogs, rule.SubjectTags);
            bool obstaclesExist = AnyArchetype(catalogs, rule.ObstacleTags);
            if (!subjectsExist) issues.Add(RuleIssue(rule, "subject", rule.SubjectTags));
            if (!obstaclesExist) issues.Add(RuleIssue(rule, "obstacle", rule.ObstacleTags));
            if (!subjectsExist || !obstaclesExist) continue;

            var plan = new RulePlan(rule);
            foreach (Placed p in placed)
            {
                if (HasAnyTag(p.Piece.Archetype.Tags, rule.SubjectTags))
                {
                    plan.Subjects.Add(p);
                    p.Piece.NeedsSamples = true;
                }
                if (HasAnyTag(p.Piece.Archetype.Tags, rule.ObstacleTags))
                {
                    plan.Obstacles.Add(p);
                    p.Piece.NeedsSolid = true;
                }
            }
            plans.Add(plan);
        }

        var geometries = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        foreach (Piece piece in pieces.Values.Where(p => p.NeedsSamples || p.NeedsSolid)
                     .OrderBy(p => p.Archetype.Id, StringComparer.Ordinal))
        {
            if (piece.Prepare(geometries, meshes, settings.CellSize) is { } reason)
                issues.Add(new TileWorldIssue(MeshCode,
                    $"archetype '{piece.Archetype.Id}' mesh '{piece.Archetype.MeshRef}' cannot be checked: {reason}",
                    null, null));
        }

        foreach (RulePlan plan in plans)
            CheckRule(doc, plan, issues);
        return issues;
    }

    // Every loaded object the check can place: its archetype is in the catalogs and it is not waived. Waived objects
    // pair with nothing, so dropping them here also spares loading a mesh only they use.
    static List<Placed> PlacedObjects(TileWorldDocument doc, TileWorldCatalogs catalogs, string? waiverTag,
        Dictionary<string, Piece> pieces)
    {
        var placed = new List<Placed>();
        foreach (TileObject o in doc.AllObjects())
        {
            if (catalogs.Archetype(o.ArchetypeId) is not { } archetype) continue;
            if (waiverTag is not null && o.Tags is { } tags && tags.Contains(waiverTag, StringComparer.Ordinal)) continue;
            if (!pieces.TryGetValue(archetype.Id, out Piece? piece))
                pieces.Add(archetype.Id, piece = new Piece(archetype));
            placed.Add(new Placed(o, piece));
        }
        return placed;
    }

    static void CheckRule(TileWorldDocument doc, RulePlan plan, List<TileWorldIssue> issues)
    {
        List<Placed> subjects = Ready(doc, plan.Subjects);
        List<Placed> obstacles = Ready(doc, plan.Obstacles);
        if (subjects.Count == 0 || obstacles.Count == 0) return;

        // Obstacles are id-ordered, so every bucket list ascends by id and sorting candidate indices sorts by id.
        var buckets = new Dictionary<(long X, long Z), List<int>>();
        var wide = new List<int>();
        for (int i = 0; i < obstacles.Count; i++)
        {
            BucketRange r = BucketRange.Of(obstacles[i]);
            if (r.Span > MaxBucketSpan)
            {
                wide.Add(i);
                continue;
            }
            for (long bz = r.MinZ; bz <= r.MaxZ; bz++)
                for (long bx = r.MinX; bx <= r.MaxX; bx++)
                {
                    if (!buckets.TryGetValue((bx, bz), out List<int>? list))
                        buckets.Add((bx, bz), list = new List<int>());
                    list.Add(i);
                }
        }

        var seen = new int[obstacles.Count];
        Array.Fill(seen, -1);
        var candidates = new List<int>();
        for (int s = 0; s < subjects.Count; s++)
        {
            Placed subject = subjects[s];
            candidates.Clear();
            void Consider(int i)
            {
                if (seen[i] == s) return;
                seen[i] = s;
                if (PlanarOverlap(subject, obstacles[i])) candidates.Add(i);
            }

            BucketRange r = BucketRange.Of(subject);
            if (r.Span > MaxBucketSpan)
            {
                for (int i = 0; i < obstacles.Count; i++) Consider(i);
            }
            else
            {
                for (long bz = r.MinZ; bz <= r.MaxZ; bz++)
                    for (long bx = r.MinX; bx <= r.MaxX; bx++)
                        if (buckets.TryGetValue((bx, bz), out List<int>? list))
                            foreach (int i in list) Consider(i);
                foreach (int i in wide) Consider(i);
            }

            candidates.Sort();
            foreach (int i in candidates)
            {
                Placed obstacle = obstacles[i];
                if (obstacle.Object.Id == subject.Object.Id) continue;
                if (ClearanceProbe.Deepest(subject.Piece.Samples!, subject.ToWorld, obstacle.Piece.Solid!,
                        obstacle.ToWorld, plan.Rule.Tolerance) is { } hit)
                    issues.Add(OverlapIssue(plan.Rule, subject.Object, obstacle.Object, hit.Depth));
            }
        }
    }

    // The side's objects whose geometry built, placed in the world and ordered by id.
    static List<Placed> Ready(TileWorldDocument doc, List<Placed> side)
    {
        var ready = new List<Placed>(side.Count);
        foreach (Placed p in side)
        {
            if (p.Piece.Failed) continue;
            p.Place(doc);
            ready.Add(p);
        }
        ready.Sort((a, b) => a.Object.Id.CompareTo(b.Object.Id));
        return ready;
    }

    static bool PlanarOverlap(Placed a, Placed b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;

    static bool AnyArchetype(TileWorldCatalogs catalogs, IReadOnlyList<string> tags)
    {
        foreach (TileObjectArchetype archetype in catalogs.Archetypes.Values)
            if (HasAnyTag(archetype.Tags, tags)) return true;
        return false;
    }

    static bool HasAnyTag(List<string>? tags, IReadOnlyList<string> wanted)
    {
        if (tags is null) return false;
        foreach (string tag in wanted)
            if (tags.Contains(tag, StringComparer.Ordinal)) return true;
        return false;
    }

    static TileWorldIssue RuleIssue(TileClearanceRule rule, string side, IReadOnlyList<string> tags) =>
        new(RuleCode, $"rule '{rule.Name}': {side} tags [{string.Join(", ", tags)}] match no archetype in the catalogs",
            null, null);

    static TileWorldIssue OverlapIssue(TileClearanceRule rule, TileObject subject, TileObject obstacle, float depth) =>
        new(OverlapCode,
            string.Create(CultureInfo.InvariantCulture,
                $"rule '{rule.Name}': {subject.ArchetypeId} #{subject.Id} at ({subject.X}, {subject.Z}) plane {subject.Plane} is {depth:0.00} m inside {obstacle.ArchetypeId} #{obstacle.Id} at ({obstacle.X}, {obstacle.Z}) plane {obstacle.Plane}"),
            RegionCoord.Of(subject.X, subject.Z), subject.Coord);

    sealed class RulePlan
    {
        public RulePlan(TileClearanceRule rule) => Rule = rule;
        public TileClearanceRule Rule { get; }
        public List<Placed> Subjects { get; } = new();
        public List<Placed> Obstacles { get; } = new();
    }

    // One archetype's part in this call: which builds its rules need, and the geometry once built. A failure of any
    // needed build fails the archetype, so all of its pairs are skipped and it is reported once.
    sealed class Piece
    {
        public Piece(TileObjectArchetype archetype) => Archetype = archetype;
        public TileObjectArchetype Archetype { get; }
        public bool NeedsSamples { get; set; }
        public bool NeedsSolid { get; set; }
        public bool Failed { get; private set; }
        public ClearanceMesh? Mesh { get; private set; }
        public ClearanceSamples? Samples { get; private set; }
        public ClearanceSolid? Solid { get; private set; }

        // Null when every needed build succeeded, otherwise why not.
        public string? Prepare(Dictionary<string, Geometry> geometries, ITileMeshGeometry meshes, float cellSize)
        {
            string? reason = Build(geometries, meshes, cellSize);
            Failed = reason is not null;
            return reason;
        }

        string? Build(Dictionary<string, Geometry> geometries, ITileMeshGeometry meshes, float cellSize)
        {
            if (string.IsNullOrWhiteSpace(Archetype.MeshRef)) return "the archetype has no meshRef";
            if (!geometries.TryGetValue(Archetype.MeshRef, out Geometry? geometry))
                geometries.Add(Archetype.MeshRef, geometry = Geometry.Load(meshes, Archetype.MeshRef));
            if (geometry.Mesh is null) return geometry.LoadError;

            Mesh = geometry.Mesh;
            if (NeedsSolid)
            {
                Solid = geometry.SolidAt(cellSize, out string? solidError);
                if (Solid is null) return solidError;
            }
            if (NeedsSamples) Samples = geometry.SamplesAt(cellSize);
            return null;
        }
    }

    // One meshRef's geometry, shared by every archetype that names it. The solid and samples build on first use.
    sealed class Geometry
    {
        ClearanceSolid? _solid;
        string? _solidError;
        ClearanceSamples? _samples;

        Geometry(ClearanceMesh? mesh, string? loadError)
        {
            Mesh = mesh;
            LoadError = loadError;
        }

        public ClearanceMesh? Mesh { get; }
        public string? LoadError { get; }

        public static Geometry Load(ITileMeshGeometry meshes, string meshRef)
        {
            try
            {
                return meshes.TryLoad(meshRef, out ClearanceMesh? mesh, out string? error)
                    ? new Geometry(mesh, null)
                    : new Geometry(null, error ?? "the mesh could not be read");
            }
            catch (ArgumentException e)
            {
                // ClearanceMesh refuses a loader's bad geometry (a non-finite position, an index out of range) by
                // throwing, and a refused mesh is an authoring finding, never a crash of the check.
                return new Geometry(null, e.Message);
            }
        }

        public ClearanceSolid? SolidAt(float cellSize, out string? error)
        {
            if (_solid is null && _solidError is null)
            {
                try
                {
                    _solid = ClearanceSolid.Build(Mesh!, cellSize);
                }
                catch (ClearanceTooLargeException e)
                {
                    _solidError = e.Message;
                }
            }
            error = _solidError;
            return _solid;
        }

        public ClearanceSamples SamplesAt(float cellSize) => _samples ??= ClearanceSamples.Build(Mesh!, cellSize);
    }

    // One object on one side of a rule. Placement is computed once, the first time a rule needs it.
    sealed class Placed
    {
        bool _placed;

        public Placed(TileObject o, Piece piece)
        {
            Object = o;
            Piece = piece;
        }

        public TileObject Object { get; }
        public Piece Piece { get; }
        public Matrix4x4 ToWorld { get; private set; }
        public Vector2 Min { get; private set; }
        public Vector2 Max { get; private set; }

        public void Place(TileWorldDocument doc)
        {
            if (_placed) return;
            ToWorld = TileObjectPlacement.LocalToWorld(doc, Piece.Archetype, Object);
            (Min, Max) = ClearanceProbe.PlanarBounds(Piece.Mesh!.Min, Piece.Mesh.Max, ToWorld);
            _placed = true;
        }
    }

    // The inclusive bucket rectangle a placed object's planar bounds cover. Indices are clamped and the span is
    // counted in double, so an absurd but finite extent cannot overflow either.
    readonly record struct BucketRange(long MinX, long MinZ, long MaxX, long MaxZ)
    {
        const double MaxIndex = 1L << 40;

        public double Span => (MaxX - MinX + 1d) * (MaxZ - MinZ + 1d);

        public static BucketRange Of(Placed p) =>
            new(Bucket(p.Min.X), Bucket(p.Min.Y), Bucket(p.Max.X), Bucket(p.Max.Y));

        static long Bucket(float metres) =>
            (long)Math.Clamp(Math.Floor(metres / (double)BucketMetres), -MaxIndex, MaxIndex);
    }
}
