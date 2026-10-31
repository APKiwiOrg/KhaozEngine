using System;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace KhaozEngine.Windowing;

// Recorder-owned append-only arena. Only unfinished rows may be updated. No scene storage escapes.
[JsonConverter(typeof(PointShadowAttributionSerialization))]
public sealed partial class PointShadowAttributionCapture
{
    readonly ulong[] _motionKeys;
    readonly Guid _epoch;
    readonly PointAttributionLimits _limits;
    readonly ulong _hashMask;
    readonly PointAttributionInput[] _inputs;
    readonly int[] _inputLookup;
    int _inputsUsed;
    readonly PointAttributionFrame[] _frames;
    readonly PointAttributionRow[] _rows;
    readonly PointAttributionCaster[] _casters;
    readonly PointAttributionSpan[] _spans;
    readonly PointAttributionContributor[] _references;
    readonly PointAttributionContributor[] _scratch;
    readonly int[] _casterLookup, _spanLookup;
    int _framesUsed, _rowsUsed, _publishedRows, _castersUsed, _spansUsed, _referencesUsed, _scratchUsed;
    bool _active;
    long _sequence, _sceneFrame;
    int _rowStart;

    public PointShadowAttributionCapture(Guid epoch, PointAttributionLimits? limits = null)
        : this(epoch, limits ?? PointAttributionLimits.Default, ulong.MaxValue) { }

    internal PointShadowAttributionCapture(Guid epoch, PointAttributionLimits limits, ulong hashMask)
    {
        var max = PointAttributionLimits.Default;
        if (epoch == Guid.Empty) throw new ArgumentException("An epoch is required.", nameof(epoch));
        Validate(limits.Callbacks, max.Callbacks); Validate(limits.Requests, max.Requests);
        Validate(limits.Rows, max.Rows); Validate(limits.Casters, max.Casters);
        Validate(limits.References, max.References); Validate(limits.Spans, max.Spans);
        Validate(limits.Contributors, max.Contributors); Validate(limits.Inputs, max.Inputs);
        _epoch = epoch; _limits = limits; _hashMask = hashMask;
        _motionKeys = new ulong[limits.Casters];
        _inputs = new PointAttributionInput[limits.Inputs];
        _inputLookup = new int[LookupSize(limits.Inputs)];
        _frames = new PointAttributionFrame[limits.Callbacks];
        _rows = new PointAttributionRow[limits.Rows];
        _casters = new PointAttributionCaster[limits.Casters];
        _spans = new PointAttributionSpan[limits.Spans];
        _references = new PointAttributionContributor[limits.References];
        _scratch = new PointAttributionContributor[limits.Contributors];
        _casterLookup = new int[LookupSize(limits.Casters)];
        _spanLookup = new int[LookupSize(limits.Spans)];
        AllocatedBytes = Bytes(_frames) + Bytes(_rows) + Bytes(_casters) + Bytes(_spans)
            + Bytes(_references) + Bytes(_scratch) + Bytes(_casterLookup) + Bytes(_spanLookup)
            + Bytes(_inputs) + Bytes(_inputLookup) + Bytes(_motionKeys) + 512;
        if (AllocatedBytes >= 64 * 1024 * 1024) throw new ArgumentException("Attribution arena exceeds its byte budget.");
    }

    static void Validate(int value, int max)
    {
        if (value < 1 || value > max) throw new ArgumentOutOfRangeException(nameof(value));
    }
    static int LookupSize(int capacity) => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capacity * 2);
    static long Bytes<T>(T[] values) where T : struct => 32L + (long)Unsafe.SizeOf<T>() * values.Length;
    public long AllocatedBytes { get; }
    public Guid Epoch => _epoch;
    public PointAttributionLimits Limits => _limits;
    public PointAttributionCapacity Overflow { get; private set; }
    public long OverflowSequence { get; private set; }
    public bool Frozen { get; private set; }
    // Scratch is never serialized or referenced by published contributor records.
    public Span<ulong> MotionKeys => Frozen || Overflow != PointAttributionCapacity.None ? default : _motionKeys;
    public bool CheckMotionKeyCapacity(Guid epoch, long sequence, int groupedCount)
    {
        if (Frozen || epoch != _epoch || sequence <= 0 || sequence < _sequence) return false;
        if (groupedCount > _motionKeys.Length && Overflow == PointAttributionCapacity.None)
        {
            Overflow = PointAttributionCapacity.MotionKeys;
            OverflowSequence = sequence;
        }
        return Overflow == PointAttributionCapacity.None;
    }
    public int InputCount => _inputsUsed;
    public PointAttributionInput GetInput(int index) => _inputs[Checked(index, _inputsUsed)];
    public int FrameCount => _framesUsed;
    public int RowCount => _publishedRows;
    public int CasterCount => _castersUsed;
    public int SpanCount => _spansUsed;
    public int ReferenceCount => _referencesUsed;
    bool Retaining => _active && !Frozen && Overflow == PointAttributionCapacity.None;

    public bool BeginFrame(Guid epoch, long sequence, long sceneFrame)
    {
        if (Frozen || _active || epoch != _epoch || sequence <= _sequence || sceneFrame < 1) return false;
        _sequence = sequence;
        if (Overflow != PointAttributionCapacity.None) return false;
        if (_framesUsed == _frames.Length) { Exhaust(PointAttributionCapacity.Callbacks); return false; }
        _active = true; _sceneFrame = sceneFrame; _rowStart = _rowsUsed; _scratchUsed = 0;
        return true;
    }

    void Exhaust(PointAttributionCapacity capacity)
    {
        if (Overflow != PointAttributionCapacity.None) return;
        Overflow = capacity; OverflowSequence = _sequence;
    }

    public int AddRow(in PointAttributionRow row)
    {
        if (!Retaining) return -1;
        if (_rowsUsed - _rowStart == _limits.Requests) { Exhaust(PointAttributionCapacity.Requests); return -1; }
        if (_rowsUsed == _rows.Length) { Exhaust(PointAttributionCapacity.Rows); return -1; }
        _rows[_rowsUsed] = row;
        return _rowsUsed++;
    }

    public PointAttributionRow PendingRow(int index) => Retaining && index >= _rowStart && index < _rowsUsed ? _rows[index] : default;
    public void UpdateRow(int index, in PointAttributionRow row)
    {
        if (Retaining && index >= _rowStart && index < _rowsUsed) _rows[index] = row;
    }
    public void BeginSpan() => _scratchUsed = 0;

    public int AddContributor(in PointAttributionCaster caster, int locator0, int locator1)
    {
        if (!Retaining) return -1;
        if (_scratchUsed == _scratch.Length) { Exhaust(PointAttributionCapacity.Contributors); return -1; }
        int id = InternCaster(caster);
        if (id >= 0) _scratch[_scratchUsed++] = new(id, locator0, locator1);
        return id;
    }

    public int EndSpan(int rigidCount)
    {
        if (!Retaining) return -1;
        if (rigidCount < 0 || rigidCount > _scratchUsed) throw new ArgumentOutOfRangeException(nameof(rigidCount));
        ulong hash = (uint)rigidCount;
        for (int i = 0; i < _scratchUsed; i++)
        {
            var item = _scratch[i];
            hash = Fold(Fold(Fold(hash, (uint)item.Caster), (uint)item.Locator0), (uint)item.Locator1);
        }
        int bucket = (int)(hash & _hashMask & (uint)(_spanLookup.Length - 1));
        while (_spanLookup[bucket] != 0)
        {
            int id = _spanLookup[bucket] - 1;
            var span = _spans[id];
            if (span.RigidCount == rigidCount && span.Count == _scratchUsed
                && _references.AsSpan(span.Start, span.Count).SequenceEqual(_scratch.AsSpan(0, _scratchUsed))) return id;
            bucket = (bucket + 1) & (_spanLookup.Length - 1);
        }
        if (_spansUsed == _spans.Length) { Exhaust(PointAttributionCapacity.Spans); return -1; }
        if (_scratchUsed > _references.Length - _referencesUsed) { Exhaust(PointAttributionCapacity.References); return -1; }
        int result = _spansUsed++;
        _spans[result] = new(_referencesUsed, _scratchUsed, rigidCount);
        _scratch.AsSpan(0, _scratchUsed).CopyTo(_references.AsSpan(_referencesUsed));
        _referencesUsed += _scratchUsed;
        _spanLookup[bucket] = result + 1;
        return result;
    }

    public PointAttributionFrame EndFrame(PointAttributionAvailability availability, bool complete = true)
    {
        if (!_active) return default;
        if (Overflow != PointAttributionCapacity.None) { availability = PointAttributionAvailability.Overflow; complete = false; }
        var frame = new PointAttributionFrame(_epoch, _sequence, _sceneFrame, _rowStart, _rowsUsed - _rowStart,
            _castersUsed, _spansUsed, _referencesUsed, InputCount, availability, complete);
        _frames[_framesUsed++] = frame;
        _publishedRows = _rowsUsed;
        _active = false;
        return frame;
    }
    public void Freeze()
    {
        if (Frozen) return;
        if (_active) EndFrame(PointAttributionAvailability.Abandoned, false);
        Frozen = true;
    }
    public PointAttributionRow GetRow(int index) => _rows[Checked(index, _publishedRows)];
    public PointAttributionCaster GetCaster(int index) => _casters[Checked(index, _castersUsed)];
    public PointAttributionSpan GetSpan(int index) => _spans[Checked(index, _spansUsed)];
    public PointAttributionContributor GetContributor(int index) => _references[Checked(index, _referencesUsed)];
    public PointAttributionFrame GetFrame(int index) => _frames[Checked(index, _framesUsed)];
    static int Checked(int index, int count) => (uint)index < (uint)count ? index : throw new ArgumentOutOfRangeException(nameof(index));
}
