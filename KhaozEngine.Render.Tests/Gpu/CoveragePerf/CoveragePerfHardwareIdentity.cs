using System;
using System.Globalization;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Reported IDs establish hardware identity. The whole adapter model corroborates the route.</summary>
internal static class CoveragePerfHardwareIdentity
{
    internal const string NameBased = "name-based (PCI vendor/device IDs not reported)";
    internal const string PciBased = "PCI-based (reported vendor/device IDs with adapter-name corroboration)";

    internal static string Basis(GpuCapabilities caps)
        => caps.PciVendorId.HasValue && caps.PciDeviceId.HasValue ? PciBased : NameBased;

    internal static string FormatId(uint? value)
        => value is uint id ? "0x" + id.ToString("x4", CultureInfo.InvariantCulture) : "unavailable";

    internal static bool IsT4(CoveragePerfSessionInfo info)
    {
        if (info.SoftwareAdapter || !Regex.IsMatch(info.Adapter.Trim(), @"\A(?:NVIDIA\s+)?(?:Tesla\s+)?T4\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (HasId(info.Vendor) || HasId(info.Device) || info.HardwareIdentityBasis == PciBased)
            return info.HardwareIdentityBasis == PciBased && ReadId(info.Vendor) == 0x10de && ReadId(info.Device) == 0x1eb8;
        return info.HardwareIdentityBasis == NameBased && info.Vendor.Trim().Equals("NVIDIA", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLavapipe(CoveragePerfSessionInfo info)
    {
        if (!info.SoftwareAdapter || !Regex.IsMatch(info.Adapter.Trim(), @"\A(?:lavapipe|llvmpipe)(?:\s+\([^)]*\))?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (HasId(info.Vendor) || HasId(info.Device) || info.HardwareIdentityBasis == PciBased)
            return info.HardwareIdentityBasis == PciBased && ReadId(info.Vendor) == 0x10005 && ReadId(info.Device).HasValue;
        return info.HardwareIdentityBasis == NameBased && info.Vendor.Trim().Equals("Mesa", StringComparison.OrdinalIgnoreCase);
    }

    static bool HasId(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    static uint? ReadId(string value) => HasId(value) && uint.TryParse(value.AsSpan(2),
        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint id) ? id : null;
}
