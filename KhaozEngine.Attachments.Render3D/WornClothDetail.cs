using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// Carry the difference from material pose to the shown surface. Proxy nodes already carry the motion blend.
internal sealed class WornClothDetail(BoundClothSurface profile, ClothWorkLimits? displayLimits = null)
{
    private readonly ClothDetailedReconstruction _candidate = new(profile);
    private readonly ClothDetailedReconstruction? _cheapTargetBase = profile.HasMaterialMapping ? new(profile) : null;
    private readonly Matrix4x4[] _cheapComposed = profile.HasMaterialMapping ? new Matrix4x4[profile.JointCount] : [];
    private readonly ClothDetailedSweep? _sweep = profile.HasMaterialMapping
        ? new(profile, displayLimits ?? ClothWorkLimits.LayeredContact) : null;
    private ModelVertex[][] _shown = ClothDetailedReconstruction.Allocate(profile);
    private ClothDetailResidual[][] _carried = ClothDetailResidual.Allocate(profile);
    private ClothDetailResidual[][] _next = ClothDetailResidual.Allocate(profile);
    private Matrix4x4 _model;
    private Vector3 _origin;
    private bool _hasOpening;

    internal ModelVertex[][] Shown => _shown;

    internal void Reset() => _hasOpening = false;

    internal bool Seed(BoundClothSurface surface, ReadOnlySpan<Vector3> nodes, in Matrix4x4 model, Vector3 origin,
        ReadOnlySpan<Matrix4x4> hostPalette, ReadOnlySpan<Matrix4x4> cheapPalette)
    {
        if (!_candidate.Material(surface, nodes, model, hostPalette)) return false;
        surface.ComposeGarment(cheapPalette, _cheapComposed);
        Transfer(surface, Matrix4x4.Identity, liveTarget: false, amount: 1, copyOpening: false);
        Publish(model, origin);
        return true;
    }

    internal ClothStepResult Stage(BoundClothSurface surface, ReadOnlySpan<Vector3> nodes, ReadOnlySpan<Vector3> fallbackNodes,
        in Matrix4x4 model, Vector3 origin, ReadOnlySpan<Matrix4x4> hostPalette, ReadOnlySpan<Matrix4x4> cheapPalette,
        bool liveTarget, float amount, bool sameMaterialPose, in ClothSubstep support, ClothStepResult prior,
        bool closingOnly = false)
    {
        if (!surface.HasMaterialMapping)
            return _candidate.Fitted(surface, nodes, model, hostPalette) ? default : Failure(ClothStepStatus.CollapsedMaterial);
        if (!_candidate.Material(surface, nodes, model, hostPalette))
            return Failure(ClothStepStatus.CollapsedMaterial);
        if (!liveTarget)
        {
            surface.ComposeGarment(cheapPalette, _cheapComposed);
            if (amount < 1 && !_cheapTargetBase!.Material(surface, fallbackNodes, model, hostPalette))
                return Failure(ClothStepStatus.CollapsedMaterial);
        }

        Matrix4x4 openingModel = model;
        ModelVertex[][] opening = _candidate.Storage;
        Matrix4x4 orientation = Matrix4x4.Identity;
        // A fresh support pose has no matching opening geometry. Keep the old publication until this pose is accepted.
        bool sweepOpening = _hasOpening && !closingOnly;
        if (sweepOpening)
        {
            openingModel = _model;
            openingModel.Translation += _origin - origin;
            opening = _shown;
            if (amount < 1 && openingModel != model)
            {
                if (!Matrix4x4.Invert(model, out Matrix4x4 inverse)) return Failure(ClothStepStatus.NonfiniteState);
                orientation = openingModel * inverse;
                orientation.Translation = Vector3.Zero;
            }
        }
        bool copyOpening = sweepOpening && amount == 0 && sameMaterialPose && openingModel == model;
        Transfer(surface, orientation, liveTarget, amount, copyOpening);
        return _sweep!.Verify(surface, opening, openingModel, _candidate.Storage, model, support, prior);
    }

    private void Transfer(BoundClothSurface surface, in Matrix4x4 orientation, bool liveTarget, float amount, bool copyOpening)
    {
        for (int material = 0; material < surface.Materials.Length; material++)
        {
            BoundClothMaterial view = surface.Materials[material];
            Span<ModelVertex> output = _candidate.Vertices(material);
            if (liveTarget && amount == 1)
            {
                Array.Clear(_next[material]);
                continue;
            }

            for (int i = 0; i < output.Length; i++)
            {
                bool moves = view.Moves(i);
                ModelVertex cheap = !liveTarget && (moves || amount == 1) ?
                    SkinningMath.SkinVertex(view.Vertices[i], _cheapComposed) : default;
                if (!moves)
                {
                    if (!liveTarget && amount == 1) output[i] = cheap;
                    continue;
                }

                ModelVertex materialPose = output[i];
                if (copyOpening)
                    output[i] = _shown[material][i];
                else if (amount == 1)
                    output[i] = cheap;
                else
                {
                    ClothDetailResidual residual = _hasOpening ? _carried[material][i].InBasis(orientation) : default;
                    ClothDetailResidual destination = liveTarget ? default :
                        new(cheap, _cheapTargetBase!.Vertices(material)[i]);
                    output[i] = ClothDetailResidual.Lerp(residual, destination, amount).Apply(materialPose);
                }
                _next[material][i] = new(output[i], materialPose);
            }
        }
    }

    internal void Publish(in Matrix4x4 model, Vector3 origin)
    {
        _shown = _candidate.Exchange(_shown);
        (_carried, _next) = (_next, _carried);
        _model = model;
        _origin = origin;
        _hasOpening = true;
    }

    private static ClothStepResult Failure(ClothStepStatus status) => new(status, 0, 0, 0, 0);
}
