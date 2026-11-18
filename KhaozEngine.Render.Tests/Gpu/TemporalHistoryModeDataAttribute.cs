using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Render3D;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Run the same image contract in both history modes without duplicating its case roster.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TemporalHistoryModeDataAttribute(params object[] values) : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        yield return [.. values, TemporalHistoryDetail.Display];
        yield return [.. values, TemporalHistoryDetail.Fine];
    }
}
