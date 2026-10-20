using System;
using System.Globalization;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Creation-fixed adapter identity. Failed queries remain unanswered independently.</summary>
internal readonly record struct D3D11AdapterIdentity(uint? VendorId, uint? DeviceId, string? DriverVersion)
{
    internal static D3D11AdapterIdentity Read(Func<(uint Vendor, uint Device)> readIds, Func<long?> readDriver)
    {
        uint? vendor = null, device = null;
        string? driver = null;
        try { (vendor, device) = readIds(); }
        catch { /* DXGI did not answer. Zero would be a different answer. */ }
        try
        {
            if (readDriver() is long version)
            {
                ulong bits = unchecked((ulong)version);
                driver = string.Create(CultureInfo.InvariantCulture,
                    $"{(bits >> 48) & 0xffff}.{(bits >> 32) & 0xffff}.{(bits >> 16) & 0xffff}.{bits & 0xffff}");
            }
        }
        catch { /* Driver version is optional even when the adapter description succeeded. */ }
        return new(vendor, device, driver);
    }
}
