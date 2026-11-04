using System;
using System.IO;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class TerrainCandidateCodecTests
{
    [Theory]
    [InlineData("count")]
    [InlineData("redundant-varint")]
    [InlineData("overflow")]
    [InlineData("outside")]
    [InlineData("unadmitted")]
    [InlineData("kind")]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    public void StoredEndpointsAreCanonicalBoundedAndAdmittedBeforeUse(string fault)
    {
        // #1697. Stored lists replace regeneration and must reject malformed endpoints themselves.
        NavLink[] links = [new(0, 0, 0, 0, 1, 0) { Kind = NavLinkKind.Drop }];
        NavTraversalLayer[] layers = [new(2, 1, [true, true], [0, 0])];
        byte[] bytes = TerrainCandidateCodec.Encode(links, 2, 1);
        Assert.Equal(links, TerrainCandidateCodec.Decode(bytes, layers, 1));
        int expected = 1;
        if (fault == "count") bytes[0] = 127;
        else if (fault == "redundant-varint") bytes = [129, 0, .. bytes.AsSpan(1)];
        else if (fault == "overflow") bytes = [1, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2, .. bytes.AsSpan(2)];
        else if (fault == "outside") bytes[2] = 4;
        else if (fault == "unadmitted") layers = [new(2, 1, [true, false], [0, 0])];
        else if (fault == "kind") bytes[3] = 255;
        else if (fault == "duplicate") { bytes = TerrainCandidateCodec.Encode([links[0], links[0]], 2, 1); expected = 2; }
        else bytes = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => TerrainCandidateCodec.Decode(bytes, layers, expected));
    }
}
