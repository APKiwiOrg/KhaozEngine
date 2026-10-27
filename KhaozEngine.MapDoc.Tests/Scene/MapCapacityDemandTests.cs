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
            var observed = type.GetProperty(nameof(CapacityDemandDiagnostics.IsObserved))!.GetMethod!.CreateDelegate<Func<bool>>();
            RuntimeHelpers.PrepareDelegate(observed);
            RuntimeHelpers.PrepareMethod(observed.Method.MethodHandle);
            bool result = true;
            long allocated = AllocationMeasurement.Measure("first unobserved diagnostic use", () => result = observed());
            Assert.False(result);
            Assert.Equal(0, allocated);
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
