using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>One empty selected-geometry and dry-medium certificate. The shared kernel still selects support,
/// pays climb and maps seats at each rounded trial axis. Unenclosed operations retire the whole shortcut.</summary>
internal sealed class CertifiedGroundRangeQueries(MovementQueryLease lease, SupportedGroundRegion region,
    MoveTuning tuning, MovementSelection selection, MovementContactSupportBinding binding,
    Vector2 minimum, Vector2 maximum, Vector3 lower, Vector3 upper)
{
    internal bool Failed { get; private set; }
    internal ContactCapsuleShapes Shapes => lease.Workspace.Shapes;
    internal void Begin() => Failed = false;
    internal void Refuse() => Failed = true;
    internal Vector3 KernelFeet(Vector3 centre, in MoveTuning knobs) => new(centre.X, centre.Y - knobs.CapsuleHalfHeight, centre.Z);
    internal Vector3 BodyCentre(Vector3 feet) => new(feet.X, feet.Y + tuning.CapsuleHalfHeight, feet.Z);
    internal Vector3 SupportedBodyCentre(Vector3 feet, in SupportSample support)
    {
        var ground = Analytic(feet, out var translation);
        if (ground.Height != support.Height || ground.Normal != support.Normal) Decline();
        return new(feet.X, SupportedCapsulePose.Centre(feet.Y, tuning.CapsuleHalfHeight, translation: translation), feet.Z);
    }
    internal float HeightAt(Vector3 feet) => Analytic(feet, out _).Height;
    internal Vector3 NormalAt(Vector3 feet) => Analytic(feet, out _).Normal;
    FootAnalyticGround Analytic(Vector3 feet, out SupportHeightTranslation translation)
    {
        var query = new FootSupportQuery(new(feet.X, feet.Z), feet.Y,
            tuning.FootRadiusFraction * tuning.CapsuleRadius, tuning.StepHeight, tuning.StepHeight,
            MathF.Cos(tuning.MaxSlopeRadians));
        CheckSupport(query);
        return Sample(query.Axis, out translation);
    }
    FootAnalyticGround Sample(Vector2 axis, out SupportHeightTranslation translation)
    {
        Require(region.Sample(axis, out var ground, out translation));
        _ = SupportedCapsulePose.Centre(ground.Height, tuning.CapsuleHalfHeight, translation: translation);
        var fact = new MovementAnalyticGround(axis, ground.Height, ground.Normal, binding.Owner,
            selection.Space, null, lease.Identity)
        { HeightTranslation = translation };
        Require(lease.Workspace.RememberAnalytic(selection.Space, axis, fact));
        return ground;
    }
    internal SupportSample FindSupport(in FootSupportQuery query, out MovementContactSupportBinding? owner)
    {
        try
        {
            CheckSupport(query);
            FootAnalyticGround ground = Sample(query.Axis, out _);
            SupportSample support = FootSupport.Find(ground, null, null, query);
            if (support.Status != SupportStatus.Walkable) Decline();
            owner = binding;
            return support;
        }
        // GroundSignals catches optional support failures. A consumed refusal still invalidates this proof,
        // since the live producer may answer the same query outside the captured geometry or clearance.
        catch (ContactQueryFailure) { Failed = true; throw; }
    }
    internal MovementContactSupportBinding BindSupport(Vector3 feet, in SupportSample support, float radius, float gate)
    {
        CheckSupport(new(new(feet.X, feet.Z), feet.Y, radius, tuning.StepHeight, tuning.StepHeight, MathF.Cos(gate)));
        var ground = Sample(new(feet.X, feet.Z), out _);
        if (support.Static.HasValue || ground.Height != support.Height || ground.Normal != support.Normal) Decline();
        return binding;
    }
    void CheckSupport(in FootSupportQuery query)
    {
        var request = new MovementSupportRequest(Body(new(query.Axis.X, query.FeetY, query.Axis.Y)),
            query.ReachUp, query.ReachDown, MathF.Acos(query.CosMaxSlope),
            new(selection.Space, new(query.Axis.X, query.FeetY, query.Axis.Y)));
        Require(lease.CheckContactSupportQuery(request, query));
        if (!SupportFootprintBounds.TryCreate(FootSupport.DescribeFootprint(query), out var bounds) ||
            !Contains(bounds.Minimum, bounds.Maximum)) Decline();
    }
    internal bool SweepShell(CapsuleShape shape, Vector3 centre, Vector3 direction, float distance,
        out SweepHit hit, QueryFilter filter)
    {
        hit = default;
        if (filter != QueryFilter.StaticsOnly || !float.IsFinite(distance) || distance < 0) Decline();
        CheckShell(shape, centre, direction * distance);
        return false;
    }
    internal bool PenetrationShell(CapsuleShape shape, Vector3 centre, out Vector3 mtv)
    {
        mtv = default;
        CheckShell(shape, centre, Vector3.Zero);
        return false;
    }
    void CheckShell(CapsuleShape shape, Vector3 centre, Vector3 delta)
    {
        var body = new MovementBodyQuery(centre, shape.Radius,
            GroundPlacement.NotBelow((double)shape.Radius + (double)shape.Length / 2), selection.Space, selection.Support);
        if (!MovementCapsuleRounding.TryEnclose(body, Vector3.Zero, out var shell, out _, out _)) Decline();
        CheckBody(shell, delta, 2 * MovementQueryLease.CoverageSkinMetres);
    }
    internal bool AcceptSegment(Vector3 from, Vector3 to)
    {
        MovementBodyQuery body = Body(from);
        CheckBody(body, BodyCentre(to) - body.Centre, MovementQueryLease.CoverageSkinMetres);
        return true;
    }
    internal MovementWaterPoint SampleMedium(Vector3 feet)
    {
        AcceptSegment(feet, feet);
        return new(MovementAvailability.Known, selection.Space, null, false, 1, null);
    }
    MovementBodyQuery Body(Vector3 feet) => new(BodyCentre(feet), tuning.CapsuleRadius,
        tuning.CapsuleHalfHeight, selection.Space, selection.Support);
    void CheckBody(in MovementBodyQuery body, Vector3 delta, float skin)
    {
        Require(lease.CheckContactBodyPath(body, delta));
        Vector3 centre = body.Centre;
        double x = (double)centre.X + delta.X, y = (double)centre.Y + delta.Y, z = (double)centre.Z + delta.Z;
        double radial = (double)body.Radius + skin, vertical = (double)body.HalfHeight + skin;
        if (centre.X < minimum.X || centre.X > maximum.X || centre.Z < minimum.Y || centre.Z > maximum.Y ||
            x < minimum.X || x > maximum.X || z < minimum.Y || z > maximum.Y ||
            Math.Min(centre.X, x) - radial < lower.X || Math.Max(centre.X, x) + radial > upper.X ||
            Math.Min(centre.Y, y) - vertical < lower.Y || Math.Max(centre.Y, y) + vertical > upper.Y ||
            Math.Min(centre.Z, z) - radial < lower.Z || Math.Max(centre.Z, z) + radial > upper.Z) Decline();
    }
    bool Contains(Vector3 min, Vector3 max) =>
        min.X >= lower.X && min.Y >= lower.Y && min.Z >= lower.Z &&
        max.X <= upper.X && max.Y <= upper.Y && max.Z <= upper.Z;
    void Require(MovementAvailability status)
    {
        if (status == MovementAvailability.Known) return;
        Failed = true;
        throw new ContactQueryFailure(status);
    }
    void Decline()
    {
        Failed = true;
        throw new ContactQueryFailure(MovementAvailability.Unresolved);
    }
}
