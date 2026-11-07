using System;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static partial class FoliageFixtures
{
    // Shared by inline and stored schema parity, retaining the diagnostics the old fragments checked.
    internal static void AssertPayloadIdentifiers(string variant, MapDocumentException error)
    {
        if (variant == "format-ten")
        {
            Assert.Equal(10, error.ActualVersion);
            Assert.Equal(new[] { MapDocumentFile.CurrentFormatVersion }, error.SupportedVersions);
        }
        if (variant is "set-payload-two" or "layer-payload-two" or "policy-version")
        {
            Assert.Equal(2, error.ActualVersion);
            Assert.Equal(new[] { 1 }, error.SupportedVersions);
        }
        if (variant is "policy-id" or "policy-version") Assert.Equal("legacy-tile", error.ExpectedPolicyId);
        if (variant is "rule-id-duplicate") Assert.Equal("door-rule", error.EntityId);
        if (variant is "mask-id-duplicate" or "mask-rect-reversed" or "mask-surface-blank")
            Assert.Equal("roof-mask", error.EntityId);
        if (variant is "owner-space-blank" or "owner-surface-blank") Assert.Equal("house-1", error.EntityId);
        if (variant is "density-1023" or "density-1025")
        {
            Assert.Equal(variant == "density-1023" ? 1023 : 1025, error.ActualByteCount);
            Assert.Equal(1024, error.ExpectedByteCount);
        }
        if (variant == "density-token-ceiling")
        {
            Assert.Equal(1372, error.ActualCount);
            Assert.Equal(1368, error.Limit);
        }
        if (variant.StartsWith("ceiling-", StringComparison.Ordinal))
        {
            long limit = variant switch
            {
                "ceiling-layers" or "ceiling-models" => 256,
                "ceiling-allowed" or "ceiling-materials" => 65535,
                "ceiling-rules" or "ceiling-masks" => 4096,
                "ceiling-owners" => 65536,
                "ceiling-chunks" => 16384,
                "ceiling-density" => 67108864,
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
            Assert.Equal(limit, error.Limit);
            if (variant == "ceiling-density") Assert.Equal(limit + 1024, error.ActualByteCount);
            else Assert.Equal(limit + 1, error.ActualCount);
        }
    }
}
