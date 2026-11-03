using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

public sealed partial class PhysicsNavBake
{
    // The serial order of a contact build, with phases as barriers: setup (snapshot, workers, first pins), admission
    // in captured Z, X and raw-surface order, layering and link generation, edges in layer, Z, X and neighbor order,
    // then Stair links in candidate index order with skipped candidates included. Each work item carries its ordinal
    // in that order, so the lowest failing ordinal is the exception the serial build throws first.
    private GroundNavigation BuildContactProfileInParallel(GroundMoveContext context, EffectiveMovementBinding source,
        in MoveTuning tuning, NavAreaFootprint footprint, float height, int maxSurfaces, int degree,
        ContactNavigationReusePolicy reuse, float maxDropMetres)
    {
        ContactNavigationWorkers workers = ContactNavigationWorkers.Create(this, context, source, footprint, degree,
            Columns.Height, reuse, maxDropMetres);
        GroundNavigation navigation;
        try
        {
            var admission = new ParallelContactAdmission(this, workers, tuning, maxSurfaces);
            NavSpace space = NavLayerBaker.BakeGroundedLayered(admission,
                Options.MinX, Options.MinZ, Options.MaxX, Options.MaxZ, Options.CellSize,
                tuning.StepHeight, height, maxSurfaces, maxLayerCells: Options.MaxLayerCells);
            admission.Sources.BindLayers(space);
            space = ContactNavigationProof.WithRestingHeights(space, admission.Anchors);

            var nodes = new bool[space.Layers.Count][];
            var exits = new byte[space.Layers.Count][];
            var layerRows = new int[space.Layers.Count + 1];
            int width = 0;
            for (int layer = 0; layer < space.Layers.Count; layer++)
            {
                NavGrid grid = space.Layers[layer];
                int count = checked(grid.Width * grid.Height);
                nodes[layer] = new bool[count];
                exits[layer] = new byte[count];
                for (int z = 0; z < grid.Height; z++)
                    for (int x = 0; x < grid.Width; x++)
                        nodes[layer][z * grid.Width + x] = grid.IsPassable(x, z, 0f);
                layerRows[layer + 1] = checked(layerRows[layer] + grid.Height);
                width = Math.Max(width, grid.Width);
            }

            workers.Run(ContactNavigationPhase.Edges, layerRows[^1],
                row => (long)row * width * ProfileNeighbors.Length, (proof, band) =>
            {
                for (int row = band.Start, layer = 0; row < band.End; row++)
                {
                    while (row >= layerRows[layer + 1]) layer++;
                    NavGrid grid = space.Layers[layer];
                    int z = row - layerRows[layer];
                    for (int x = 0; x < grid.Width; x++)
                    {
                        if (!nodes[layer][z * grid.Width + x]) continue;
                        using IDisposable? group = admission.Sources.TryGet(layer, x, z, out var address)
                            ? proof.BeginSource(address) : null;
                        for (int direction = 0; direction < ProfileNeighbors.Length; direction++)
                        {
                            int nx = x + ProfileNeighbors[direction].X, nz = z + ProfileNeighbors[direction].Z;
                            if (!grid.InBounds(nx, nz) || !nodes[layer][nz * grid.Width + nx]) continue;
                            if (!band.Admits(((long)row * width + x) * ProfileNeighbors.Length + direction)) return;
                            if (proof.TryEdge(Feet(grid, x, z), Feet(grid, nx, nz)))
                                exits[layer][z * grid.Width + x] |= (byte)(1 << direction);
                        }
                    }
                }
            }).ThrowSelected();

            var layers = new NavTraversalLayer[space.Layers.Count];
            for (int layer = 0; layer < layers.Length; layer++)
                layers[layer] = new NavTraversalLayer(space.Layers[layer].Width, space.Layers[layer].Height,
                    nodes[layer], exits[layer]);

            IReadOnlyList<NavLink> candidates = space.Links;
            var accepted = new bool[candidates.Count];
            workers.Run(ContactNavigationPhase.Links, candidates.Count, index => index, (proof, band) =>
            {
                for (int index = band.Start; index < band.End; index++)
                {
                    if (!band.Admits(index)) return;
                    NavLink link = candidates[index];
                    if (link.Kind != NavLinkKind.Stair) continue;
                    if (!layers[link.FromLayer].IsAccepted(link.FromX, link.FromZ) ||
                        !layers[link.ToLayer].IsAccepted(link.ToX, link.ToZ)) continue;
                    accepted[index] = proof.TryEdge(Feet(space.Layers[link.FromLayer], link.FromX, link.FromZ),
                        Feet(space.Layers[link.ToLayer], link.ToX, link.ToZ));
                }
            }).ThrowSelected();
            var links = new List<NavLink>();
            for (int index = 0; index < candidates.Count; index++)
                if (accepted[index]) links.Add(candidates[index]);

            if (maxDropMetres > 0)
            {
                var drops = ContactDropCandidates.Generate(space.Layers, layers, tuning, maxDropMetres);
                space = new NavSpace(space.Layers, [.. space.Links, .. drops]);
                var dropAccepted = new bool[drops.Count];
                workers.Run(ContactNavigationPhase.Drops, drops.Count, index => index, (proof, band) =>
                {
                    for (int index = band.Start; index < band.End; index++)
                    {
                        if (!band.Admits(index)) return;
                        dropAccepted[index] = proof.TryDrop(space, drops[index]);
                    }
                }).ThrowSelected();
                for (int index = 0; index < drops.Count; index++)
                    if (dropAccepted[index]) links.Add(drops[index]);
            }
            workers.Snapshot.AssertCurrent();
            _ = Context;
            var graph = new NavTraversalGraph(space, tuning.CapsuleRadius, height, layers, links);
            navigation = new GroundNavigation(graph, tuning, footprint, false, maxDropMetres);
        }
        catch (Exception failure)
        {
            workers.Release(failure);
            throw;
        }
        workers.Release(null);
        return navigation;
    }

    private static void RequireConcurrentReads(GroundMoveContext context)
    {
        if (context.Physics is not IPhysicsConcurrentReadSource || context.MovementQueries is not IPhysicsConcurrentReadSource)
            throw new NotSupportedException("A parallel contact navigation build needs IPhysicsConcurrentReadSource on " +
                "the physics source and on the selected query view.");
    }

    /// <summary>Admits every captured column on the workers when the layered bake asks for its first column, then
    /// replays the accepted surfaces in the bake's row-major order. Grid validation therefore keeps its place before
    /// admission, and layer extraction its place after it.</summary>
    private sealed class ParallelContactAdmission(PhysicsNavBake bake, ContactNavigationWorkers workers,
        MoveTuning tuning, int maxSurfaces) : INavColumnProvider
    {
        readonly Dictionary<Vector3, float> _anchors = [];
        int[]? _starts;
        NavSurfaceSample[]? _accepted;
        int _cursor;

        internal IReadOnlyDictionary<Vector3, float> Anchors => _anchors;
        internal ContactNavigationSources Sources { get; } = new();

        public int SampleColumn(float x, float z, Span<NavSurfaceSample> surfaces)
        {
            if (_starts is null) Admit();
            int width = bake.Columns.Width, cell = _cursor++;
            if (cell >= bake.Columns.Width * bake.Columns.Height ||
                BitConverter.SingleToUInt32Bits(x) != BitConverter.SingleToUInt32Bits(CellX(cell % width)) ||
                BitConverter.SingleToUInt32Bits(z) != BitConverter.SingleToUInt32Bits(CellZ(cell / width)))
                throw new InvalidOperationException("The layered bake sampled columns outside the admission order.");
            ReadOnlySpan<NavSurfaceSample> column = _accepted.AsSpan(_starts![cell], _starts[cell + 1] - _starts[cell]);
            column.CopyTo(surfaces);
            return column.Length;
        }

        float CellX(int x) => bake.Options.MinX + (x + 0.5f) * bake.Options.CellSize;
        float CellZ(int z) => bake.Options.MinZ + (z + 0.5f) * bake.Options.CellSize;

        void Admit()
        {
            int width = bake.Columns.Width, rows = bake.Columns.Height;
            var bands = new ContactAdmissionBand?[ContactNavigationWorkers.BandCount(workers.Count, rows)];
            ContactNavigationCutoff cutoff = workers.Run(ContactNavigationPhase.Admission, rows,
                row => (long)row * width * maxSurfaces, (proof, band) =>
            {
                var result = bands[band.Index] = new ContactAdmissionBand(band.Start * width, band.End * width);
                var buffer = new NavSurfaceSample[maxSurfaces];
                for (int z = band.Start; z < band.End; z++)
                    for (int x = 0; x < width; x++)
                    {
                        int cell = z * width + x;
                        float cellX = CellX(x), cellZ = CellZ(z);
                        int count = bake.Columns.SampleColumn(cellX, cellZ, buffer), kept = 0;
                        for (int i = 0; i < count; i++)
                        {
                            long ordinal = (long)cell * maxSurfaces + i;
                            if (!band.Admits(ordinal)) return;
                            NavSurfaceSample surface = buffer[i];
                            if (surface.Headroom < 2f * tuning.CapsuleHalfHeight) continue;
                            Vector3 feet = new(cellX, surface.Height, cellZ);
                            float next = i + 1 < count ? buffer[i + 1].Height : float.PositiveInfinity;
                            if (!proof.Admit(feet, surface.Headroom, next)) continue;
                            result.Sources.Add((feet, new(x, z, i, BitConverter.SingleToInt32Bits(surface.Height)), ordinal));
                            buffer[kept++] = surface;
                            result.Anchors.Add((feet, proof.Anchors[feet], ordinal));
                        }
                        result.Keep(cell, buffer.AsSpan(0, kept));
                    }
            });
            ContactAdmissionBand.MergeAnchors(bands, cutoff, _anchors);
            ContactAdmissionBand.MergeSources(bands, cutoff, Sources);
            cutoff.ThrowSelected();

            int cells = width * rows;
            _starts = new int[cells + 1];
            var accepted = new List<NavSurfaceSample>();
            foreach (ContactAdmissionBand? band in bands)
                for (int cell = band!.FirstCell; cell < band.EndCell; cell++)
                {
                    _starts[cell] = accepted.Count;
                    accepted.AddRange(band.Column(cell));
                }
            _starts[cells] = accepted.Count;
            _accepted = [.. accepted];
        }
    }
}

/// <summary>One admission band's accepted surfaces and anchors, in its cells' order.</summary>
internal sealed class ContactAdmissionBand(int firstCell, int endCell)
{
    readonly int[] _starts = new int[endCell - firstCell + 1];
    readonly List<NavSurfaceSample> _surfaces = [];

    internal int FirstCell { get; } = firstCell;
    internal int EndCell { get; } = endCell;
    internal List<(Vector3 Feet, float Resting, long Ordinal)> Anchors { get; } = [];
    internal List<(Vector3 Feet, ContactNavigationRawSource Raw, long Ordinal)> Sources { get; } = [];

    internal static void MergeSources(ContactAdmissionBand?[] bands, ContactNavigationCutoff cutoff,
        ContactNavigationSources sources)
    {
        foreach (ContactAdmissionBand? band in bands)
        {
            if (band is null) return;
            foreach (var source in band.Sources)
            {
                if (!cutoff.Admits(source.Ordinal)) return;
                sources.Record(source.Feet, source.Raw);
            }
        }
    }

    internal void Keep(int cell, ReadOnlySpan<NavSurfaceSample> surfaces)
    {
        _starts[cell - FirstCell] = _surfaces.Count;
        _surfaces.AddRange(surfaces);
        _starts[cell - FirstCell + 1] = _surfaces.Count;
    }

    internal ReadOnlySpan<NavSurfaceSample> Column(int cell) => CollectionsMarshal.AsSpan(_surfaces)
        [_starts[cell - FirstCell].._starts[cell - FirstCell + 1]];

    /// <summary>Adds every band's anchors to <paramref name="anchors"/> by band index, which is row order and so
    /// admission order, never completion order. A duplicate key fails at the later insertion's ordinal, as the serial
    /// <c>Dictionary.Add</c> does, and only wins when it lies below every failure already in the cutoff. Merging stops at
    /// the first anchor at or above the cutoff and at the first band never claimed, since every later one lies above
    /// the cutoff too.</summary>
    internal static void MergeAnchors(ContactAdmissionBand?[] bands, ContactNavigationCutoff cutoff,
        Dictionary<Vector3, float> anchors)
    {
        foreach (ContactAdmissionBand? band in bands)
        {
            if (band is null) return;
            foreach ((Vector3 feet, float resting, long ordinal) in band.Anchors)
            {
                if (!cutoff.Admits(ordinal)) return;
                try { anchors.Add(feet, resting); }
                catch (ArgumentException duplicate)
                {
                    cutoff.Fail(ordinal, duplicate);
                    return;
                }
            }
        }
    }
}
