using System;
using System.Globalization;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>A caller allowance and the aggregate demand that first exceeded it. Counts are never truncated.</summary>
public sealed record MapSceneCapacity(string LimitName, decimal Requested, long Allowance)
{
    public string Message => string.Create(CultureInfo.InvariantCulture,
        $"Scene capacity exceeded for {LimitName}: requested {Requested}, allowance {Allowance}.");

    internal static MapSceneCapacity? From(Exception error) => error switch
    {
        MapSceneCapacityException capacity => capacity.Capacity,
        MapSceneSourceException source => source.Capacity,
        _ => error.InnerException is { } inner ? From(inner) : null,
    };
}

/// <summary>A capacity refusal that retains its limit and exact demand, including sums beyond Int64.</summary>
public sealed class MapSceneCapacityException(string limitName, decimal requested, long allowance)
    : Exception(new MapSceneCapacity(limitName, requested, allowance).Message)
{
    public MapSceneCapacity Capacity { get; } = new(limitName, requested, allowance);
    public string LimitName => Capacity.LimitName;
    public decimal Requested => Capacity.Requested;
    public long Allowance => Capacity.Allowance;
}
