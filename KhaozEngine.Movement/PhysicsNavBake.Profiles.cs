using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class PhysicsNavBake
{
    private static readonly (int X, int Z)[] ProfileNeighbors =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>Bakes one capsule class against the captured area tags and unchanged static physics.
    /// The slope must equal the capture slope. Every admitted hold, directed neighbor and Stair link
    /// is proved through the movement core at unit pace. Legacy proofs use dry medium and raw feet. Ordinary contact
    /// proofs use their water policy and certified resting anchors. The result owns pure data.</summary>
    public GroundNavigation BuildProfile(in MoveTuning tuning, NavAreaFilter areas)
        => BuildProfile(tuning, areas, GroundProfileOptions.Default);

    /// <summary>Bakes one capsule class as <see cref="BuildProfile(in MoveTuning, NavAreaFilter)"/> does. With
    /// <see cref="GroundProfileOptions.Aquatic"/> the profile reads the aquatic column view, where each swim-deep
    /// column holds one float surface at the body's resting swim height. Float holds and every edge or Stair link with
    /// a float endpoint are proved by swim steps through the live context and its medium, at unit walk pace, with each
    /// slice capped at the capsule radius and cleared against statics. Other proofs stay dry.
    /// <para>Every proof shares the capture's <see cref="PhysicsNavBakeOptions.MaxEdgeProbeSteps"/> budget, and an edge
    /// that runs past it is refused. A bank edge between a wading node and a float node walks at unit pace slowed by
    /// the wade ramp and the medium's zone scale, then swims. It needs about
    /// <c>wading length / (EdgeProbeSeconds x 1 m/s x WadeMinSpeedScale x zone scale)</c> steps while it wades, plus
    /// <c>swimming length / min(CapsuleRadius, EdgeProbeSeconds x SwimSpeed x zone scale)</c> steps once it swims,
    /// plus a few steps of final approach. The swim term needs <c>SwimSpeed x zone scale</c> above zero. A zero swim
    /// pace refuses every float edge. At the default 64 steps of 1/30 s and a zone scale of 1 that covers banks
    /// between 0.25 m cells. Larger cells or slower zones must raise
    /// <see cref="PhysicsNavBakeOptions.MaxEdgeProbeSteps"/>, or the float layer is cut off from the land. Dry bank
    /// edges on a smooth sloped physics shoreline are also subject to KhaozEngine #1265.</para></summary>
    /// <exception cref="ArgumentException">An aquatic profile's capture did not sample water, or its swim fractions
    /// are not ordered <c>Exit &lt;= Submersion &lt;= Enter</c>.</exception>
    public GroundNavigation BuildProfile(in MoveTuning tuning, NavAreaFilter areas, GroundProfileOptions options)
        => BuildProfile(tuning, areas, options, null);

    /// <summary>Bakes one capsule class as <see cref="BuildProfile(in MoveTuning, NavAreaFilter, GroundProfileOptions)"/>
    /// does, scheduled by <paramref name="execution"/>. A null execution or a degree of 1 runs the serial build. Above
    /// degree 1 an ordinary contact profile proves on worker threads over one build snapshot and produces the same
    /// profile, and its first failure in serial order is the exception the serial build throws. Legacy profiles always
    /// build serially. <see cref="NavBakeExecution.UseStableLegacyReadScope"/> optionally reuses one authenticated
    /// physics read lease for a legacy profile. See <see cref="NavBakeExecution"/> for provider obligations.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The execution's degree is below 1. This is checked first.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="NotSupportedException">A contact profile above degree 1 has a physics source or selected
    /// query view without <see cref="KhaozEngine.Physics.IPhysicsConcurrentReadSource"/>. Checked after every other
    /// argument and before any worker exists. Also when a stable legacy scope lacks
    /// <see cref="KhaozEngine.Physics.IPhysicsQueryLeaseSource"/> on its exact source or selected view.</exception>
    /// <exception cref="InvalidOperationException">The build snapshot changed during a parallel contact build,
    /// the context has live runtime statics, a stable legacy scope's owner or captured basis differs, or its backend
    /// refuses a nested explicit lease.</exception>
    public GroundNavigation BuildProfile(in MoveTuning tuning, NavAreaFilter areas, GroundProfileOptions? options,
        NavBakeExecution? execution)
    {
        int degree = NavBakeExecution.Degree(execution, nameof(execution));
        using ProfileCheck check = CheckProfile(tuning, areas, options);
        EffectiveMovementBindingKind kind = check.Kind;
        GroundMoveContext context = check.Context;
        EffectiveMovementBinding? source = check.Source;
        float height = check.Height;
        bool aquatic = check.Aquatic;
        PhysicsNavColumns columns = check.Columns;
        NavAreaFootprint footprint = check.Footprint;
        ContactNavigationReusePolicy reuse = ContactNavigationReusePolicy.Capture(execution);
        if (degree > 1 && kind == EffectiveMovementBindingKind.OrdinaryContact)
        {
            RequireConcurrentReads(context);
            return BuildContactProfileInParallel(context, source!, tuning, footprint, height,
                Options.MaxSurfacesPerColumn, degree, reuse, options!.MaxDropMetres);
        }
        using LegacyNavigationReadScope? stable = kind == EffectiveMovementBindingKind.Legacy &&
            execution?.UseStableLegacyReadScope == true ? LegacyNavigationReadScope.Acquire(context, Basis, Origin) : null;
        using ContactNavigationProof? contact = kind == EffectiveMovementBindingKind.OrdinaryContact
            ? new(this, context, source!, footprint, reuse, options!.MaxDropMetres) : null;
        var sources = new ContactNavigationSources();
        var candidates = new CandidateColumns(this, context, tuning, footprint, columns, contact, sources);
        // A swim-deep column with no surface below the water gains one surface.
        int maxSurfaces = aquatic ? Options.MaxSurfacesPerColumn + 1 : Options.MaxSurfacesPerColumn;
        NavSpace space = NavLayerBaker.BakeGroundedLayered(candidates,
            Options.MinX, Options.MinZ, Options.MaxX, Options.MaxZ, Options.CellSize,
            tuning.StepHeight, height, maxSurfaces, maxLayerCells: Options.MaxLayerCells);
        if (contact is not null)
        {
            sources.BindLayers(space);
            space = contact.WithRestingHeights(space);
        }

        var nodes = new bool[space.Layers.Count][];
        var exits = new byte[space.Layers.Count][];
        bool[][]? floats = aquatic ? new bool[space.Layers.Count][] : null;
        for (int layer = 0; layer < space.Layers.Count; layer++)
        {
            NavGrid grid = space.Layers[layer];
            int count = checked(grid.Width * grid.Height);
            nodes[layer] = new bool[count];
            exits[layer] = new byte[count];
            if (floats is not null) floats[layer] = new bool[count];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    nodes[layer][z * grid.Width + x] = grid.IsPassable(x, z, 0f);
                    if (floats is not null && nodes[layer][z * grid.Width + x])
                        floats[layer][z * grid.Width + x] = IsFloatNode(columns, grid, x, z);
                }
        }

        var layers = new NavTraversalLayer[space.Layers.Count];
        for (int layer = 0; layer < space.Layers.Count; layer++)
        {
            NavGrid grid = space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!nodes[layer][z * grid.Width + x]) continue;
                    using IDisposable? group = contact is not null && sources.TryGet(layer, x, z, out var address)
                        ? contact.BeginSource(address) : null;
                    for (int direction = 0; direction < ProfileNeighbors.Length; direction++)
                    {
                        int nx = x + ProfileNeighbors[direction].X, nz = z + ProfileNeighbors[direction].Z;
                        if (!grid.InBounds(nx, nz) || !nodes[layer][nz * grid.Width + nx]) continue;
                        if (contact is not null ? contact.TryEdge(Feet(grid, x, z), Feet(grid, nx, nz)) :
                            Prove(context, tuning, Feet(grid, x, z), Floats(floats, layer, grid, x, z),
                                Feet(grid, nx, nz), Floats(floats, layer, grid, nx, nz), footprint))
                            exits[layer][z * grid.Width + x] |= (byte)(1 << direction);
                    }
                }
            layers[layer] = new NavTraversalLayer(grid.Width, grid.Height, nodes[layer], exits[layer]);
        }
        var links = new List<NavLink>();
        foreach (NavLink link in space.Links)
        {
            if (link.Kind != NavLinkKind.Stair) continue;
            if (!layers[link.FromLayer].IsAccepted(link.FromX, link.FromZ) ||
                !layers[link.ToLayer].IsAccepted(link.ToX, link.ToZ)) continue;
            NavGrid fromGrid = space.Layers[link.FromLayer], toGrid = space.Layers[link.ToLayer];
            bool fromFloats = Floats(floats, link.FromLayer, fromGrid, link.FromX, link.FromZ);
            bool toFloats = Floats(floats, link.ToLayer, toGrid, link.ToX, link.ToZ);
            if (contact is not null ? contact.TryEdge(Feet(fromGrid, link.FromX, link.FromZ),
                Feet(toGrid, link.ToX, link.ToZ)) :
                Prove(context, tuning, Feet(fromGrid, link.FromX, link.FromZ), fromFloats,
                    Feet(toGrid, link.ToX, link.ToZ), toFloats, footprint)) links.Add(link);
        }
        if (options!.MaxDropMetres > 0)
        {
            var drops = ContactDropCandidates.Generate(space.Layers, layers, tuning, options.MaxDropMetres);
            space = new NavSpace(space.Layers, [.. space.Links, .. drops]);
            foreach (NavLink drop in drops)
                if (contact!.TryDrop(space, drop)) links.Add(drop);
        }
        _ = Context;
        contact?.AssertCurrent();
        stable?.AssertCurrent();
        var graph = new NavTraversalGraph(space, tuning.CapsuleRadius, height, layers, links);
        return new GroundNavigation(graph, tuning, footprint, aquatic, options!.MaxDropMetres);
    }

    /// <summary>Runs every profile's argument checks in order, then the concurrent-read capability check, before any
    /// profile builds. A multi-profile build above degree 1 with a contact profile calls this, so an undeclared backend
    /// refuses before an earlier profile spends its build, and an argument failure still wins over the capability.</summary>
    internal void CheckProfiles(IReadOnlyList<NavBakeProfile> profiles)
    {
        foreach (NavBakeProfile profile in profiles)
            CheckProfile(profile.Tuning, profile.Areas, new GroundProfileOptions { Aquatic = profile.Aquatic, MaxDropMetres = profile.MaxDropMetres }).Dispose();
        RequireConcurrentReads(Context);
    }

    // Every existing argument check of one profile build, in order. The caller disposes the contact source binding.
    private ProfileCheck CheckProfile(in MoveTuning tuning, NavAreaFilter areas, GroundProfileOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EffectiveMovementBindingKind kind = new NavBakeProfile("", tuning, areas) { Aquatic = options.Aquatic, MaxDropMetres = options.MaxDropMetres }.ResolveKind();
        GroundMoveContext context = Context;
        RequireAuthoredStatics(context);
        if (kind == EffectiveMovementBindingKind.OrdinaryContact)
        {
            PhysicsNavCaptureBasis basis = Basis
                ?? throw new InvalidOperationException("Contact navigation requires a capture from an IPhysicsQueryLeaseSource.");
            basis.AssertCurrent();
        }
        EffectiveMovementBinding? source = kind == EffectiveMovementBindingKind.OrdinaryContact
            ? context.CreateNavigationBinding(tuning) : null;
        try
        {
            if (kind == EffectiveMovementBindingKind.Legacy) context.ValidateTuning(tuning);
            if (tuning.MaxSlopeRadians != Options.MaxSlopeRadians)
                throw new ArgumentException("Profile slope must equal the capture slope.", nameof(tuning));
            float height = 2f * tuning.CapsuleHalfHeight;
            if (!float.IsFinite(height))
                throw new ArgumentOutOfRangeException(nameof(tuning), "Profile height must be finite.");
            bool aquatic = options.Aquatic;
            if (aquatic && !Options.SampleWater)
                throw new ArgumentException("An aquatic profile needs a capture that sampled water.", nameof(options));
            if (aquatic && !(tuning.SwimExitDepthFraction <= tuning.SwimSurfaceSubmersionFraction &&
                tuning.SwimSurfaceSubmersionFraction <= tuning.SwimEnterDepthFraction))
                throw new ArgumentException("An aquatic profile needs swim fractions ordered exit, submersion, enter.", nameof(tuning));
            PhysicsNavColumns columns = aquatic ? AquaticColumns.Derive(Columns, tuning) : Columns;
            var footprint = new NavAreaFootprint(columns, Options, tuning, areas);
            return new(kind, context, source, height, aquatic, columns, footprint);
        }
        catch
        {
            source?.Dispose();
            throw;
        }
    }

    private readonly record struct ProfileCheck(EffectiveMovementBindingKind Kind, GroundMoveContext Context,
        EffectiveMovementBinding? Source, float Height, bool Aquatic, PhysicsNavColumns Columns,
        NavAreaFootprint Footprint) : IDisposable
    {
        public void Dispose() => Source?.Dispose();
    }

    private bool Prove(GroundMoveContext context, in MoveTuning tuning, Vector3 from, bool fromFloats,
        Vector3 to, bool toFloats, NavAreaFootprint footprint)
    {
        _ = Context;
        bool accepted = fromFloats || toFloats
            ? SwimTraversalProbe.TryEdge(context, tuning, from, fromFloats, to, toFloats, footprint.AcceptsPredicate,
                Options.EdgeProbeSeconds, Options.MaxEdgeProbeSteps)
            : GroundTraversalProbe.TryEdge(context, tuning, from, to, footprint.AcceptsPredicate,
                Options.EdgeProbeSeconds, Options.MaxEdgeProbeSteps);
        _ = Context;
        return accepted;
    }

    private static bool Floats(bool[][]? floats, int layer, NavGrid grid, int x, int z)
        => floats is not null && floats[layer][z * grid.Width + x];

    // Layering copies candidate heights unchanged, so the node's derived surface is the one with bit-equal height.
    private static bool IsFloatNode(PhysicsNavColumns columns, NavGrid grid, int x, int z)
    {
        uint height = BitConverter.SingleToUInt32Bits(grid.SurfaceHeightAt(x, z)!.Value);
        ReadOnlySpan<PhysicsNavSurface> column = columns.GetColumn(x, z);
        for (int i = 0; i < column.Length; i++)
            if (BitConverter.SingleToUInt32Bits(column[i].Height) == height) return columns.IsFloat(x, z, i);
        return false;
    }

    private static Vector3 Feet(NavGrid grid, int x, int z)
    {
        Vector2 center = grid.CellCenter(x, z);
        return new Vector3(center.X, grid.RestingHeightAt(x, z)!.Value, center.Y);
    }

    private sealed class CandidateColumns(PhysicsNavBake bake, GroundMoveContext context,
        MoveTuning tuning, NavAreaFootprint footprint, PhysicsNavColumns columns,
        ContactNavigationProof? contact, ContactNavigationSources sources) : INavColumnProvider
    {
        public int SampleColumn(float x, float z, Span<NavSurfaceSample> surfaces)
        {
            int count = columns.SampleColumn(x, z, surfaces), accepted = 0;
            for (int i = 0; i < count; i++)
            {
                NavSurfaceSample surface = surfaces[i];
                if (surface.Headroom < 2f * tuning.CapsuleHalfHeight) continue;
                Vector3 feet = new(x, surface.Height, z);
                bool floats = columns.IsFloatAt(x, z, i);
                float next = i + 1 < count ? surfaces[i + 1].Height : float.PositiveInfinity;
                if (contact is not null ? contact.Admit(feet, surface.Headroom, next) :
                    bake.Prove(context, tuning, feet, floats, feet, floats, footprint))
                {
                    if (contact is not null)
                        sources.Record(feet, new((int)MathF.Floor((x - bake.Options.MinX) / bake.Options.CellSize),
                            (int)MathF.Floor((z - bake.Options.MinZ) / bake.Options.CellSize), i,
                            BitConverter.SingleToInt32Bits(surface.Height)));
                    surfaces[accepted++] = surface;
                }
            }
            return accepted;
        }
    }
}
