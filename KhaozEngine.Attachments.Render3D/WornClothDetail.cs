using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// The candidate may be refused. The published vertices must then retain the matching host pose.
internal sealed class WornClothDetail(BoundClothSurface profile)
{
    private readonly ClothDetailedReconstruction _candidate = new(profile);
    private readonly ClothDetailedSweep? _sweep = profile.HasMaterialMapping ? new(profile, ClothWorkLimits.LayeredContact) : null;
    private ModelVertex[][] _shown = ClothDetailedReconstruction.Allocate(profile);
    private Matrix4x4 _model;
    private Vector3 _origin;
    private bool _hasOpening;
    private bool _live;

    internal ModelVertex[][] Shown => _shown;

    internal void Reset() => _hasOpening = false;

    internal void Seed(BoundClothSurface surface, in Matrix4x4 model, Vector3 origin,
        ReadOnlySpan<Matrix4x4> cheapPalette)
    {
        _candidate.Cheap(surface, cheapPalette);
        Publish(model, origin, live: false);
    }

    internal ClothStepResult Stage(BoundClothSurface surface, ReadOnlySpan<Vector3> nodes,
        in Matrix4x4 model, Vector3 origin, ReadOnlySpan<Matrix4x4> hostPalette, ReadOnlySpan<Matrix4x4> cheapPalette,
        bool liveTarget, bool handoff, float amount, in ClothSubstep support)
    {
        if (!surface.HasMaterialMapping)
            return _candidate.Fitted(surface, nodes, model, hostPalette) ? default : Failure(ClothStepStatus.CollapsedMaterial);
        if (liveTarget)
        {
            if (!_candidate.Material(surface, nodes, model, hostPalette))
                return Failure(ClothStepStatus.CollapsedMaterial);
        }
        else
            _candidate.Cheap(surface, cheapPalette);

        if (!handoff && _hasOpening && _live == liveTarget) return default;

        Matrix4x4 openingModel = model;
        ModelVertex[][] opening = _candidate.Storage;
        if (_hasOpening)
        {
            openingModel = _model;
            openingModel.Translation += _origin - origin;
            opening = _shown;
            if (amount < 1)
            {
                Matrix4x4 openingToCurrent = Matrix4x4.Identity;
                if (openingModel != model)
                {
                    if (!Matrix4x4.Invert(model, out Matrix4x4 inverse)) return Failure(ClothStepStatus.NonfiniteState);
                    openingToCurrent = openingModel * inverse;
                }
                Blend(surface, openingToCurrent, amount);
            }
        }
        return _sweep!.Verify(surface, opening, openingModel, _candidate.Storage, model, support);
    }

    private void Blend(BoundClothSurface surface, in Matrix4x4 openingToCurrent, float amount)
    {
        for (int material = 0; material < surface.Materials.Length; material++)
        {
            Span<ModelVertex> target = _candidate.Vertices(material);
            foreach (var embedding in surface.Materials[material].Embeddings)
            {
                int vertex = embedding.VertexIndex;
                ref ModelVertex value = ref target[vertex];
                ModelVertex prior = _shown[material][vertex];
                if (openingToCurrent != Matrix4x4.Identity)
                {
                    prior.Position = Vector3.Transform(prior.Position, openingToCurrent);
                    prior.Normal = Rotate(prior.Normal, openingToCurrent);
                    prior.Tangent = new(Rotate(Tangent(prior), openingToCurrent), prior.Tangent.W);
                }

                if (amount == 0)
                    value = prior;
                else
                {
                    value.Position = Vector3.Lerp(prior.Position, value.Position, amount);
                    value.Normal = ClothDetailedReconstruction.Unit(Vector3.Lerp(prior.Normal, value.Normal, amount), value.Normal);
                    Vector3 targetTangent = Tangent(value);
                    value.Tangent = new(ClothDetailedReconstruction.Unit(
                        Vector3.Lerp(Tangent(prior), targetTangent, amount), targetTangent), value.Tangent.W);
                }
            }
        }
    }

    internal void Publish(in Matrix4x4 model, Vector3 origin, bool live)
    {
        _shown = _candidate.Exchange(_shown);
        _model = model;
        _origin = origin;
        _hasOpening = true;
        _live = live;
    }

    private static ClothStepResult Failure(ClothStepStatus status) => new(status, 0, 0, 0, 0);

    private static Vector3 Rotate(Vector3 direction, in Matrix4x4 transform) =>
        ClothDetailedReconstruction.Unit(Vector3.TransformNormal(direction, transform), direction);

    private static Vector3 Tangent(in ModelVertex vertex) => new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
}
