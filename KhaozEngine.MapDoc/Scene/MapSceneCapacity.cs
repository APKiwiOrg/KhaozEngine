using System;
using System.Globalization;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>A caller allowance and the aggregate demand that first exceeded it. Counts are never truncated.</summary>
public sealed record MapSceneCapacity(string LimitName, decimal Requested, long Allowance)
{
    public string Message => string.Create(CultureInfo.InvariantCulture,
        $"Scene capacity exceeded for {LimitName}: requested {Requested}, allowance {Allowance}.");

    internal MapDocumentFinding Finding => new(MapDocumentRefusal.WorkCapacityExceeded, Message)
    {
        Budget = LimitName,
        ActualCount = Requested >= long.MinValue && Requested <= long.MaxValue && decimal.Truncate(Requested) == Requested
            ? (long)Requested : null,
        Limit = Allowance,
    };

    // An unmeasured refusal keeps this exact demand on its cause but publishes a null ActualCount.
    internal MapDocumentException Refuse(string? message = null, bool measured = true) => MapDocumentRefusalFactory.FromFinding(
        measured ? Finding : Finding with { ActualCount = null }, message ?? Message, new MapSceneCapacityException(this));

    internal static MapSceneCapacity? From(Exception error) => error switch
    {
        MapSceneCapacityException capacity => capacity.Capacity,
        MapSceneSourceException source => source.Capacity,
        Storage.MapSurfaceCapacityException surface => surface.Capacity,
        Water.MapWaterCapacityException water => water.Capacity,
        _ => error.InnerException is { } inner ? From(inner) : null,
    };
}

/// <summary>A capacity refusal that retains its limit and exact demand, including sums beyond Int64.</summary>
public sealed class MapSceneCapacityException : Exception
{
    public MapSceneCapacityException(string limitName, decimal requested, long allowance) : this(new MapSceneCapacity(limitName, requested, allowance)) { }
    internal MapSceneCapacityException(MapSceneCapacity capacity) : base(capacity.Message) => Capacity = capacity;
    public MapSceneCapacity Capacity { get; }
    public string LimitName => Capacity.LimitName;
    public decimal Requested => Capacity.Requested;
    public long Allowance => Capacity.Allowance;
}
