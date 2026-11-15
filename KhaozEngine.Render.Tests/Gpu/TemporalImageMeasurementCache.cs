using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Share one capture's values between its image contract and its diagnostic table.</summary>
public sealed class TemporalImageMeasurementCache
{
    readonly Dictionary<(Type, string, TemporalUpscale, TemporalHistoryDetail), object> _values = new();

    internal T Get<T>(string family, TemporalUpscale preset, TemporalHistoryDetail detail, Func<T> capture)
        where T : notnull
    {
        var key = (typeof(T), family, preset, detail);
        if (!_values.TryGetValue(key, out object? value)) _values.Add(key, value = capture());
        return (T)value;
    }
}
