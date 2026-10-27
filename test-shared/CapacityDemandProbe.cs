using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Collects the public value-only diagnostic surface during a serial test.</summary>
internal sealed class CapacityDemandProbe : IDisposable
{
    readonly IDisposable _subscription;
    internal List<object> Events { get; } = new();
    internal bool ThrowOnEvent { get; set; }
    internal CapacityDemandProbe() => _subscription = CapacityDemandDiagnostics.Observe(value =>
    {
        Events.Add(value);
        if (ThrowOnEvent) throw new InvalidOperationException("observer failure");
    });
    internal IEnumerable<object> For(string limit) => Events.Where(e => Read<string>(e, "LimitName") == limit);
    internal static T Read<T>(object value, string name) => Assert.IsType<T>(value.GetType().GetProperty(name)!.GetValue(value));
    public void Dispose() => _subscription.Dispose();
}
