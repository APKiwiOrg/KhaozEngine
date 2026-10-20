using System;
using System.Text.Json;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class TelemetryAdapterIdentityTests
{
    [Theory]
    [InlineData(0x10deu, 0x1eb8u)]
    [InlineData(0x10005u, 0u)]
    public void AnsweredIdsRemainNumericAndDriverTextIsEscaped(uint vendor, uint device)
    {
        var info = new TelemetrySessionInfo { PciVendorId = vendor, PciDeviceId = device, DriverVersion = "driver \"1\"" };
        using var header = JsonDocument.Parse(TelemetrySessionHeader.Build(info, Array.Empty<TelemetryHeaderValue>()));
        var gpu = header.RootElement.GetProperty("session").GetProperty("gpu");
        Assert.Equal(vendor, gpu.GetProperty("pciVendorId").GetUInt32());
        Assert.Equal(device, gpu.GetProperty("pciDeviceId").GetUInt32());
        Assert.Equal("driver \"1\"", gpu.GetProperty("driverVersion").GetString());
    }

    [Fact]
    public void UnansweredIdentityIsRecordedAsNullBesideTheAdapter()
    {
        using var header = JsonDocument.Parse(TelemetrySessionHeader.Build(new TelemetrySessionInfo(), Array.Empty<TelemetryHeaderValue>()));
        var gpu = header.RootElement.GetProperty("session").GetProperty("gpu");
        foreach (string name in new[] { "pciVendorId", "pciDeviceId", "driverVersion" })
        {
            Assert.True(gpu.TryGetProperty(name, out var value), name);
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }
}
