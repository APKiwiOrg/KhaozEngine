using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using KhaozEngine.Diagnostics;
using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class MapCapacityDemandTests
{
    [Fact]
    public void SaturatedEvent_RequestedStaysAtTheDecimalCeiling()
    {
        // A saturated origin event can carry a consumed baseline beside a saturated increment.
        var demand = new CapacityDemand(1, 1, 1, 0, 1, "operation", "site", "MaxFaces", 3, decimal.MaxValue, 10, true,
            CapacityDemandLifetime.Transient, true);
        Assert.Equal(decimal.MaxValue, demand.Requested);
        Assert.Equal(decimal.MinValue, (demand with { CurrentDemand = -3, ProposedIncrement = decimal.MinValue }).Requested);
        Assert.Equal(13, (demand with { ProposedIncrement = 10 }).Requested);
    }

    [Fact]
    public void OriginProvenance_StaysOffThePublicSurface()
    {
        var diagnostics = typeof(CapacityDemandDiagnostics);
        var published = diagnostics.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(m => m.Name).ToHashSet();
        Assert.Contains("Observe", published);
        Assert.Contains("Check", published);
        Assert.Contains("Enter", published);
        foreach (string name in new[] { "Derive", "Inherit", "Origins" }) Assert.DoesNotContain(name, published);
        Assert.False(diagnostics.Assembly.GetType("KhaozEngine.Diagnostics.CapacityDemandOrigin", throwOnError: true)!.IsPublic);
        Assert.True(typeof(CapacityDemand).IsPublic);
    }

    [Fact]
    public void SharedSourceWork_ReportsSuccessAndRefusalWithoutAdmittingTheRefusedIncrement()
    {
        using var probe = new CapacityDemandProbe();
        var admission = new MapSceneAdmission(new(), foliageLimits: new(MaxSourceUtf8Bytes: 10));
        admission.BeforeInputBytes(7);
        var error = Assert.Throws<MapSceneCapacityException>(() => admission.BeforeInputBytes(5));
        Assert.Equal(12, error.Requested);
        Assert.Equal(7, admission.FoliageSourceUtf8Bytes);
        var rows = probe.For("MaxSourceUtf8Bytes").ToArray();
        Assert.Equal(2, rows.Length);
        Check(rows[0], 0, 7, 10, false);
        Check(rows[1], 7, 5, 10, true);
        Assert.Equal(Read<long>(rows[0], "AdmissionId"), Read<long>(rows[1], "AdmissionId"));
    }

    [Theory]
    [InlineData("metadata", "MaxPageMetadataChecks")]
    [InlineData("visit", "MaxPageReads")]
    [InlineData("range", "MaxPageRangeOperations")]
    [InlineData("decode", "MaxPageReads")]
    public void PageBudget_EachGateReportsItsOwnAttemptIncludingSharedViews(string site, string limit)
    {
        using var probe = new CapacityDemandProbe();
        var budget = new MapPageBudget(0);
        var shared = budget.Share();
        Action<MapPageBudget> run = site switch
        {
            "metadata" => b => b.BeforeMetadata(),
            "visit" => b => b.BeforeVisit(),
            "range" => b => b.BeforeRange(),
            _ => b => b.BeforeRead(),
        };
        bool hasOne = site is "metadata" or "range";
        if (hasOne) run(budget);
        Assert.Throws<MapSurfaceCapacityException>(() => run(shared));
        var rows = probe.For(limit).ToArray();
        Assert.Equal(hasOne ? 2 : 1, rows.Length);
        Check(rows[^1], hasOne ? 1 : 0, 1, hasOne ? 1 : 0, true);
        if (hasOne)
        {
            Check(rows[0], 0, 1, 1, false);
            Assert.Equal(Read<long>(rows[0], "AdmissionId"), Read<long>(rows[1], "AdmissionId"));
        }
    }

    [Fact]
    public void RetainedSamples_ReportBothIndependentGatesAndKeepTheirOriginalCounters()
    {
        using var probe = new CapacityDemandProbe();
        var budget = new MapFoliageRetentionBudget(2, 10);
        Assert.True(budget.TryRetain(7));
        Assert.False(budget.TryRetain(5));
        Assert.Equal(1, budget.Samples);
        Assert.Equal(7, budget.Bytes);
        var rows = probe.For("MaxQueryRetainedBytes").ToArray();
        Assert.Equal(2, rows.Length);
        Check(rows[0], 0, 7, 10, false);
        Check(rows[1], 7, 5, 10, true);
    }

    [Fact]
    public void ThrowingObserver_CannotAlterAReservationOrARefusal()
    {
        using var probe = new CapacityDemandProbe { ThrowOnEvent = true };
        var admission = new MapSceneAdmission(new(), foliageLimits: new(MaxSourceUtf8Bytes: 10));
        admission.BeforeInputBytes(7);
        Assert.Throws<MapSceneCapacityException>(() => admission.BeforeInputBytes(5));
        Assert.Equal(7, admission.FoliageSourceUtf8Bytes);
        Assert.NotEmpty(probe.Events);
    }

    [Fact]
    public void UnobservedSuccessfulGates_AddNoAllocation()
    {
        var pages = new MapPageBudget(4);
        var retention = new MapFoliageRetentionBudget(4, 32);
        pages.BeforeVisit();
        Assert.True(retention.TryRetain(1));
        AllocationMeasurement.NoPerCallAllocation("unobserved capacity gates", () =>
        {
            using var operation = CapacityDemandDiagnostics.Enter(pages, "allocation-proof");
            pages.BeforeVisit();
            retention.TryRetain(1);
        });
    }

    [Fact]
    public void FirstUnobservedUse_AllocatesNoDiagnosticStorage()
    {
        var context = new AssemblyLoadContext("unobserved-capacity", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(CapacityDemandDiagnostics).Assembly.Location);
            var type = assembly.GetType(typeof(CapacityDemandDiagnostics).FullName!)!;
            var testAssembly = context.LoadFromAssemblyPath(typeof(CapacityDemandColdPath).Assembly.Location);
            var observed = testAssembly.GetType(typeof(CapacityDemandColdPath).FullName!)!
                .GetMethod(nameof(CapacityDemandColdPath.Exercise))!.CreateDelegate<Func<bool>>();
            _ = testAssembly.GetType(typeof(CapacityDemandColdPath).FullName!)!.GetMethod(nameof(CapacityDemandColdPath.Arguments))!.Invoke(null, null);
            Assert.Same(assembly, testAssembly.GetType(typeof(CapacityDemandColdPath).FullName!)!
                .GetMethod(nameof(CapacityDemandColdPath.DiagnosticAssembly))!.Invoke(null, null));
            RuntimeHelpers.PrepareDelegate(observed);
            RuntimeHelpers.PrepareMethod(observed.Method.MethodHandle);
            bool result = true;
            long allocated = AllocationMeasurement.Measure("first unobserved diagnostic use", () => result = observed());
            Assert.False(result);
            Assert.Equal(0, allocated);
            Assert.Null(type.GetField("s_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null));
        }
        finally { context.Unload(); }
    }

    static void Check(object row, decimal current, decimal increment, decimal allowance, bool refused)
    {
        Assert.Equal(current, Read<decimal>(row, "CurrentDemand"));
        Assert.Equal(increment, Read<decimal>(row, "ProposedIncrement"));
        Assert.Equal(current + increment, Read<decimal>(row, "Requested"));
        Assert.Equal(allowance, Read<decimal>(row, "Allowance"));
        Assert.Equal(refused, Read<bool>(row, "Refused"));
        Assert.True(Read<long>(row, "AdmissionId") > 0);
        Assert.NotEmpty(Read<string>(row, "Site"));
    }
}

// Loaded into a fresh context so neither real diagnostic call has run before the allocation region.
public static class CapacityDemandColdPath
{
    public static System.Reflection.Assembly DiagnosticAssembly() => typeof(CapacityDemandDiagnostics).Assembly;
    public static string[] Arguments() => ["cold", "MaxFaces"];
    public static bool Exercise()
    {
        using var scope = CapacityDemandDiagnostics.Enter("cold");
        return CapacityDemandDiagnostics.Check(null, "cold", "MaxFaces", 0, 1, 2, false);
    }
}
