using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Windowing;

// Export runs after Freeze, outside the timed sampling interval.
internal sealed class PointShadowAttributionSerialization : JsonConverter<PointShadowAttributionCapture>
{
    public override PointShadowAttributionCapture Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new NotSupportedException("Attribution is export-only.");

    public override void Write(Utf8JsonWriter writer, PointShadowAttributionCapture value, JsonSerializerOptions options)
    {
        if (!value.Frozen) throw new InvalidOperationException("Attribution must be frozen before export.");
        JsonSerializer.Serialize(writer, new
        {
            value.Epoch,
            value.Limits,
            value.AllocatedBytes,
            value.Overflow,
            value.OverflowSequence,
            matrixOrder = "row-major-M11-M44",
            frames = Enumerable.Range(0, value.FrameCount).Select(value.GetFrame),
            rows = Enumerable.Range(0, value.RowCount).Select(index => Row(value.GetRow(index))),
            casters = Enumerable.Range(0, value.CasterCount).Select(index => Caster(value.GetCaster(index))),
            spans = Enumerable.Range(0, value.SpanCount).Select(value.GetSpan),
            inputs = Enumerable.Range(0, value.InputCount).Select(index => Input(value.GetInput(index))),
            references = Enumerable.Range(0, value.ReferenceCount).Select(value.GetContributor),
        }, options);
    }

    static object Caster(PointAttributionCaster c) => new
    {
        c.Kind,
        c.MeshIndex,
        c.MeshGeneration,
        c.CastKind,
        c.DissolveWord,
        c.MotionKey,
        matrixWords = Matrix(c.Matrix),
    };

    static object Row(PointAttributionRow r) => new
    {
        r.Key,
        r.Mode,
        r.LightIndex,
        r.Ordinal,
        r.Slot,
        r.SlotOutcome,
        r.PriorDirty,
        r.PriorLastRenderedFrame,
        r.SignatureStatus,
        r.OldSignature,
        r.NewSignature,
        r.Mismatch,
        r.DirtyAfter,
        r.Span,
        r.SelectedOrdinal,
        r.Completed,
        r.TransientSlot,
        r.TransientSelectedOrdinal,
        r.TransientCompleted,
        r.RigidDraws,
        r.FoliageDraws,
        r.DynamicSkinnedDraws,
        r.TransientSkinnedDraws,
        r.RigidInstances,
        r.FoliageInstances,
        r.Input,
    };
    static object Input(PointAttributionInput i) => new { headerWords = Header(i.Header), rawWords = Raw(i.Raw) };
    static uint[] Matrix(PointAttributionMatrix matrix)
    {
        var words = new uint[16];
        for (int i = 0; i < words.Length; i++) words[i] = matrix[i];
        return words;
    }
    static ulong[] Header(PointAttributionHeader header)
    {
        var words = new ulong[12];
        for (int i = 0; i < words.Length; i++) words[i] = header[i];
        return words;
    }
    static uint[] Raw(PointAttributionLightWords raw)
    {
        var words = new uint[11];
        for (int i = 0; i < words.Length; i++) words[i] = raw[i];
        return words;
    }
}
