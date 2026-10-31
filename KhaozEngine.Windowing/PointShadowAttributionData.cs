using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Windowing;

// Private archive contracts. All locators are callback-local, including rigid slots and foliage sources.
public enum PointAttributionAvailability { Unobserved, Available, Disabled, MissingAtlas, Empty, Abandoned, Overflow }
public enum PointAttributionCapacity { None, Callbacks, Requests, Rows, Casters, References, Spans, Contributors, Inputs, MotionKeys }
public enum PointAttributionSlotOutcome { NotAttempted, Touched, Acquired, Refused }
public enum PointAttributionSignatureStatus { NotApplicable, Evaluated, InvalidSlot, Unavailable }

[InlineArray(16)]
public struct PointAttributionMatrix { uint _word; }
[InlineArray(12)]
public struct PointAttributionHeader { ulong _word; }
[InlineArray(11)]
public struct PointAttributionLightWords { uint _word; }

public struct PointAttributionInput
{
    public PointAttributionHeader Header;
    public PointAttributionLightWords Raw;
}

public struct PointAttributionCaster
{
    public int Kind;
    public int MeshIndex;
    public int MeshGeneration;
    public uint CastKind;
    public uint DissolveWord;
    public ulong MotionKey;
    public PointAttributionMatrix Matrix;
}

public readonly record struct PointAttributionContributor(int Caster, int Locator0, int Locator1);
public readonly record struct PointAttributionSpan(int Start, int Count, int RigidCount);

public struct PointAttributionRow
{
    public long Key;
    public int Mode;
    public int LightIndex;
    public int Ordinal;
    public int Slot;
    public PointAttributionSlotOutcome SlotOutcome;
    public bool PriorDirty;
    public int PriorLastRenderedFrame;
    public PointAttributionSignatureStatus SignatureStatus;
    public ulong OldSignature;
    public ulong NewSignature;
    public bool Mismatch;
    public bool DirtyAfter;
    public int Span;
    public int SelectedOrdinal;
    public bool Completed;
    public int TransientSlot;
    public int TransientSelectedOrdinal;
    public bool TransientCompleted;
    public int RigidDraws;
    public int FoliageDraws;
    public int DynamicSkinnedDraws;
    public int TransientSkinnedDraws;
    public long RigidInstances;
    public long FoliageInstances;
    public int Input;
}

public readonly record struct PointAttributionFrame(Guid Epoch, long Sequence, long SceneFrame,
    int RowStart, int RowCount, int CasterCount, int SpanCount, int ReferenceCount, int InputCount,
    PointAttributionAvailability Availability, bool Complete);

public readonly record struct PointAttributionLimits(int Callbacks = 4096, int Requests = 128,
    int Rows = 131072, int Casters = 131072, int References = 2097152, int Spans = 32768,
    int Contributors = 16384, int Inputs = 32768)
{
    public static PointAttributionLimits Default => new(4096, 128, 131072, 131072, 2097152, 32768, 16384, 32768);
}
