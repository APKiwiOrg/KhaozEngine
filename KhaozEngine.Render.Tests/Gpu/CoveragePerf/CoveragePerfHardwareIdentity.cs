using System;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The current device seam reports names, not PCI identities. Keep that limitation explicit.</summary>
internal static class CoveragePerfHardwareIdentity
{
    internal const string NameBased = "name-based (PCI vendor/device IDs unavailable through the device seam)";

    internal static bool IsT4(CoveragePerfSessionInfo info) => !info.SoftwareAdapter &&
        info.HardwareIdentityBasis == NameBased && info.Vendor.Trim().Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(info.Adapter.Trim(), @"\A(?:NVIDIA\s+)?(?:Tesla\s+)?T4\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
