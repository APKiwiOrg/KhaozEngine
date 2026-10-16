using System;

namespace KhaozEngine.Render3D;

/// <summary>Stable equipment coverage refusal reasons. Values are explicit because diagnostics and consumers persist
/// and compare them. <see cref="None"/> is success.</summary>
public enum CoverageFailureCode
{
    None = 0,
    UnknownRegion = 1,
    IdentityMismatch = 2,
    UnsupportedSupport = 3,
    UnsupportedTransform = 4,
    CoreEscaped = 5,
    CapContact = 6,
    RecessionExceeded = 7,
    FoldedPatch = 8,
    EquipmentIntersection = 9,
    HostIntersection = 10,
    OverlappingConformers = 11,
    CapacityExceeded = 12,
    StaleFrame = 13,
    UnsupportedAdapter = 14,
}

/// <summary>Immutable, allocation-free result of a coverage preparation or validation step. A refusal names the host,
/// the equipment instance and the command, plus the relevant count and limit where one applies (zero otherwise).
/// Identifiers of zero mean not applicable. <c>default</c> and <see cref="Success"/> both mean success.</summary>
public readonly record struct CoverageFailure
{
    public static CoverageFailure Success => default;

    public CoverageFailure(CoverageFailureCode code, string? hostIdentity, uint equipmentInstanceId, uint commandId, int count, int limit)
    {
        if (code is <= CoverageFailureCode.None or > CoverageFailureCode.UnsupportedAdapter)
            throw new ArgumentOutOfRangeException(nameof(code), code, "A refusal requires a defined failure code. Use Success for no failure.");
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        Code = code;
        HostIdentity = hostIdentity;
        EquipmentInstanceId = equipmentInstanceId;
        CommandId = commandId;
        Count = count;
        Limit = limit;
    }

    public CoverageFailureCode Code { get; }
    public string? HostIdentity { get; }
    public uint EquipmentInstanceId { get; }
    public uint CommandId { get; }
    public int Count { get; }
    public int Limit { get; }
    public bool IsSuccess => Code == CoverageFailureCode.None;
}
