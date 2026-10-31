namespace KhaozEngine.Windowing;

public sealed partial class PointShadowAttributionCapture
{
    public int InternInput(in PointAttributionInput input)
    {
        if (!Retaining) return -1;
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < 12; i++) hash = Fold(hash, input.Header[i]);
        for (int i = 0; i < 11; i++) hash = Fold(hash, input.Raw[i]);
        int bucket = (int)(hash & _hashMask & (uint)(_inputLookup.Length - 1));
        while (_inputLookup[bucket] != 0)
        {
            int id = _inputLookup[bucket] - 1;
            if (Exact(_inputs[id], input)) return id;
            bucket = (bucket + 1) & (_inputLookup.Length - 1);
        }
        if (_inputsUsed == _inputs.Length) { Exhaust(PointAttributionCapacity.Inputs); return -1; }
        int result = _inputsUsed++;
        _inputs[result] = input;
        _inputLookup[bucket] = result + 1;
        return result;
    }

    static bool Exact(in PointAttributionInput a, in PointAttributionInput b)
    {
        for (int i = 0; i < 12; i++) if (a.Header[i] != b.Header[i]) return false;
        for (int i = 0; i < 11; i++) if (a.Raw[i] != b.Raw[i]) return false;
        return true;
    }

    static ulong Fold(ulong hash, ulong word) => unchecked((hash ^ word) * 1099511628211UL);

    int InternCaster(in PointAttributionCaster value)
    {
        ulong hash = Fold(Fold(Fold(Fold(Fold(Fold(14695981039346656037UL, (uint)value.Kind),
            (uint)value.MeshIndex), (uint)value.MeshGeneration), value.CastKind), value.DissolveWord), value.MotionKey);
        for (int i = 0; i < 16; i++) hash = Fold(hash, value.Matrix[i]);
        int bucket = (int)(hash & _hashMask & (uint)(_casterLookup.Length - 1));
        while (_casterLookup[bucket] != 0)
        {
            int id = _casterLookup[bucket] - 1;
            if (Exact(_casters[id], value)) return id;
            bucket = (bucket + 1) & (_casterLookup.Length - 1);
        }
        if (_castersUsed == _casters.Length) { Exhaust(PointAttributionCapacity.Casters); return -1; }
        int result = _castersUsed++;
        _casters[result] = value;
        _casterLookup[bucket] = result + 1;
        return result;
    }

    static bool Exact(in PointAttributionCaster a, in PointAttributionCaster b)
    {
        if (a.Kind != b.Kind || a.MeshIndex != b.MeshIndex || a.MeshGeneration != b.MeshGeneration
            || a.CastKind != b.CastKind || a.DissolveWord != b.DissolveWord || a.MotionKey != b.MotionKey) return false;
        for (int i = 0; i < 16; i++) if (a.Matrix[i] != b.Matrix[i]) return false;
        return true;
    }
}
