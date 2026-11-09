using System.Globalization;

namespace KhaozEngine.Render3D;

/// <summary>The decoder limit and aggregate demand that refused an admission.</summary>
public sealed record GltfDecodeCapacity(string LimitName, decimal Requested, long Allowance)
{
    public string Message => string.Create(CultureInfo.InvariantCulture,
        $"Decoder capacity exceeded for {LimitName}: requested {Requested}, allowance {Allowance}.");
}
