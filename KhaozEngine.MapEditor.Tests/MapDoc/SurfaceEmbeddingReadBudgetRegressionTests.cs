using System;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;
using static KhaozEngine.Tests.MapDoc.SurfaceEmbeddingRegressionFixtures;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceEmbeddingReadBudgetRegressionTests
{
    [Theory]
    [InlineData(true, 0, 2L)]
    [InlineData(false, 0, 2L)]
    [InlineData(true, 1, 242L)]
    [InlineData(false, 1, 242L)]
    [InlineData(true, 2, 483L)]
    [InlineData(false, 2, 483L)]
    public void ReadersCountOnlyBracketsAndInterPatchSeparators(bool useNode, int count, long exactBytes)
    {
        var accepted = new PayloadArrays(PatchBytes);
        var patches = Read(useNode, Array(count), PatchBytes, exactBytes, accepted);
        Assert.Equal(count, patches.Count);
        Assert.Equal(count, accepted.Buffers.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(new MapPatchKey("s", i, 0), patches[i].Key);
            Assert.True(patches[i].IsPresent(0, 0));
            Assert.Equal(0, patches[i].Height(0, 0));
            accepted.AssertPayload(i, i == 0 ? Patch0 : Patch1);
        }

        var refused = new PayloadArrays(PatchBytes);
        Refuses(useNode, () => Read(useNode, Array(count), PatchBytes, exactBytes - 1, refused));
        Assert.Equal(Math.Max(0, count - 1), refused.Buffers.Count);
        if (count == 2) refused.AssertPayload(0, Patch0);
    }

    [Theory]
    [InlineData(true, 259, 1000L)]
    [InlineData(false, 259, 1000L)]
    [InlineData(true, 300, 261L)]
    [InlineData(false, 300, 261L)]
    public void MalformedOverBudgetPatchRefusesBeforePayloadConstruction(bool useNode, int patchCap,
        long aggregateCap)
    {
        var output = new PayloadArrays(patchCap);
        Refuses(useNode, () => Read(useNode, "[" + MalformedPayload + "]", patchCap, aggregateCap, output));
        Assert.Empty(output.Buffers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadersUsePinnedEscapesDespiteWhitespaceAndRelaxedOuterOptions(bool useNode)
    {
        var accepted = new PayloadArrays(EscapedPatchBytes);
        var patch = Assert.Single(Read(useNode, "[" + EscapedInput + "]", EscapedPatchBytes, 276, accepted));
        Assert.Equal(new MapPatchKey("sé<\"\\\n😀", 0, 0), patch.Key);
        Assert.Single(accepted.Buffers);
        accepted.AssertPayload(0, EscapedPayload);

        var refused = new PayloadArrays(EscapedPatchBytes - 1);
        Refuses(useNode, () => Read(useNode, "[" + EscapedInput + "]", EscapedPatchBytes - 1, 276, refused));
        Assert.Empty(refused.Buffers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadersRetainTheCodecsUnescapedBase64AtItsExactBudget(bool useNode)
    {
        var output = new PayloadArrays(PatchBytes);
        var patch = Assert.Single(Read(useNode, "[" + PlusInput + "]", PatchBytes, 242, output));
        Assert.Equal(248, patch.Height(0, 0));
        Assert.Single(output.Buffers);
        output.AssertPayload(0, PlusPayload);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(PlusPayload), MapSurfacePatchCodec.Encode(patch));
    }
}
