using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    PointShadowAttributionCapture? _pointAttribution;
    PointAttributionAvailability _pointAttributionAvailability;
    int _pointAttributionRowStart, _pointAttributionRequestCount;
    bool _pointAttributionFrameActive;
    int _pointAttributionSignatureRow = -1, _pointAttributionRigidCount;
    PointAttributionInput _pointAttributionInput;
    int _pointAttributionHeaderWord;

    Span<ulong> PointAttributionMotionKeys() => _pointAttribution is { } capture ? capture.MotionKeys : default;

    void ConfigurePointAttribution(in ShadowStageProbeRequest request, PointShadowAttributionCapture? pointAttribution)
    {
        CancelPointAttribution();
        _pointAttribution = _shadowProbeActive && request.Mode == ShadowStageProbeMode.Counts ? pointAttribution : null;
    }

    void ObservePointAttributionRequests()
    {
        if (_pointAttribution is not { } capture) return;
        if (!capture.CheckMotionKeyCapacity(_shadowProbeRequest.Epoch, _shadowProbeRequest.Sequence, _instanceData.Count)) return;
        _pointAttributionFrameActive = capture.BeginFrame(_shadowProbeRequest.Epoch, _shadowProbeRequest.Sequence, Math.Max(1, _frameIndex));
        if (!_pointAttributionFrameActive) return;
        _pointAttributionRowStart = capture.RowCount;
        _pointAttributionRequestCount = _pointRequests.Count;
        _pointAttributionAvailability = !Post.Quality.Shadows.PointShadows.Enabled ? PointAttributionAvailability.Disabled
            : _pointRequests.Count == 0 ? PointAttributionAvailability.Empty
            : _pointShadowAtlas is null || _pointSlotCache is null ? PointAttributionAvailability.MissingAtlas
            : PointAttributionAvailability.Available;
        for (int i = 0; i < _pointRequests.Count; i++)
        {
            var r = _pointRequests[i];
            var input = new PointAttributionInput();
            input.Raw[0] = FloatBits(r.Position.X); input.Raw[1] = FloatBits(r.Position.Y); input.Raw[2] = FloatBits(r.Position.Z);
            input.Raw[3] = FloatBits(r.Radius); input.Raw[4] = FloatBits(r.NearRadius);
            input.Raw[5] = FloatBits(r.ExclusionMin.X); input.Raw[6] = FloatBits(r.ExclusionMin.Y); input.Raw[7] = FloatBits(r.ExclusionMin.Z);
            input.Raw[8] = FloatBits(r.ExclusionMax.X); input.Raw[9] = FloatBits(r.ExclusionMax.Y); input.Raw[10] = FloatBits(r.ExclusionMax.Z);
            capture.AddRow(new PointAttributionRow
            {
                Key = r.Key,
                Mode = (int)r.Mode,
                LightIndex = r.LightIndex,
                Ordinal = i,
                Slot = -1,
                PriorLastRenderedFrame = int.MinValue,
                Span = -1,
                SelectedOrdinal = -1,
                TransientSlot = -1,
                TransientSelectedOrdinal = -1,
                SignatureStatus = r.Mode != LightShadowMode.Static ? PointAttributionSignatureStatus.NotApplicable
                    : _pointAttributionAvailability == PointAttributionAvailability.Available
                        ? PointAttributionSignatureStatus.InvalidSlot : PointAttributionSignatureStatus.Unavailable,
                Input = capture.InternInput(input),
            });
        }
    }

    int AttributionIndex(int request) => _pointAttributionFrameActive && request >= 0 && request < _pointAttributionRequestCount
        ? _pointAttributionRowStart + request : -1;

    void ObservePointAttributionSlot(int requestIndex, PointShadowSlots cache, bool touch)
    {
        if (_pointAttribution is not { } capture || AttributionIndex(requestIndex) is < 0) return;
        int index = AttributionIndex(requestIndex);
        var row = capture.PendingRow(index);
        int slot = _pointRequests[requestIndex].Slot;
        row.Slot = slot;
        row.SlotOutcome = slot >= 0 ? touch ? PointAttributionSlotOutcome.Touched : PointAttributionSlotOutcome.Acquired
            : touch ? PointAttributionSlotOutcome.NotAttempted : PointAttributionSlotOutcome.Refused;
        row.PriorDirty = cache.IsDirty(slot); row.DirtyAfter = row.PriorDirty;
        row.PriorLastRenderedFrame = cache.LastRenderedFrame(slot);
        capture.UpdateRow(index, row);
    }

    void BeginPointAttributionSignature(int requestIndex, PointShadowSlots cache)
    {
        _pointAttributionSignatureRow = AttributionIndex(requestIndex);
        if (_pointAttribution is not { } capture || _pointAttributionSignatureRow < 0 || capture.Overflow != PointAttributionCapacity.None) return;
        var row = capture.PendingRow(_pointAttributionSignatureRow);
        row.PriorDirty = cache.IsDirty(row.Slot);
        capture.UpdateRow(_pointAttributionSignatureRow, row);
        _pointAttributionInput = capture.GetInput(row.Input);
        _pointAttributionHeaderWord = _pointAttributionRigidCount = 0;
        capture.BeginSpan();
    }

    void MixPointAttributionHeader(ref ulong hash, ulong word)
    {
        MixPointSignature(ref hash, word);
        if (_pointAttribution is not null && _pointAttributionSignatureRow >= 0 && _pointAttributionHeaderWord < 12)
            _pointAttributionInput.Header[_pointAttributionHeaderWord++] = word;
    }

    void ObservePointAttributionRigid(int slot, MeshHandle mesh, in ModelRenderer.InstanceData data, uint dissolve, uint kind)
    {
        if (_pointAttribution is not { } capture || _pointAttributionSignatureRow < 0) return;
        capture.AddContributor(new PointAttributionCaster
        {
            Kind = 0,
            MeshIndex = mesh.Index,
            MeshGeneration = mesh.Generation,
            CastKind = kind,
            DissolveWord = dissolve,
            Matrix = AttributionMatrix(data.Model),
            MotionKey = slot < capture.MotionKeys.Length ? capture.MotionKeys[slot] : 0,
        }, slot, _pointCasterIndex.RunOf(slot));
        _pointAttributionRigidCount++;
    }

    void ObservePointAttributionFoliage(int source, int proxy, MeshHandle mesh, in Matrix4x4 matrix)
    {
        if (_pointAttribution is not { } capture || _pointAttributionSignatureRow < 0) return;
        capture.AddContributor(new PointAttributionCaster
        {
            Kind = 1,
            MeshIndex = mesh.Index,
            MeshGeneration = mesh.Generation,
            Matrix = AttributionMatrix(matrix),
        }, source, proxy);
    }

    static PointAttributionMatrix AttributionMatrix(in Matrix4x4 matrix)
    {
        var result = new PointAttributionMatrix();
        var words = MemoryMarshal.Cast<Matrix4x4, uint>(MemoryMarshal.CreateReadOnlySpan(in matrix, 1));
        words.CopyTo(result);
        return result;
    }

    void EndPointAttributionSignature(int requestIndex, long signature, PointShadowSlots cache)
    {
        if (_pointAttribution is { } capture && _pointAttributionSignatureRow >= 0)
        {
            int index = AttributionIndex(requestIndex);
            var row = capture.PendingRow(index);
            row.OldSignature = unchecked((ulong)_pointCasterSignatures[row.Slot]);
            row.NewSignature = unchecked((ulong)signature);
            row.Mismatch = row.OldSignature != row.NewSignature;
            row.DirtyAfter = cache.IsDirty(row.Slot);
            row.SignatureStatus = PointAttributionSignatureStatus.Evaluated;
            row.Span = capture.EndSpan(_pointAttributionRigidCount);
            row.Input = capture.InternInput(_pointAttributionInput);
            capture.UpdateRow(index, row);
        }
        _pointAttributionSignatureRow = -1;
    }

    PointAttributionFrame? FinishPointAttribution()
    {
        if (!_pointAttributionFrameActive || _pointAttribution is not { } capture) return null;
        _pointAttributionFrameActive = false;
        return capture.EndFrame(_pointAttributionAvailability);
    }
    void CancelPointAttribution()
    {
        if (_pointAttributionFrameActive) _pointAttribution?.EndFrame(PointAttributionAvailability.Abandoned, false);
        _pointAttributionFrameActive = false; _pointAttributionSignatureRow = -1; _pointAttribution = null;
    }
}
