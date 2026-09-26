using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShaderTextTests
{
    const string Source = "#version 450\nlayout(location=0) out vec4 o;\nvoid main() {\n    o = vec4(1.0);\n}";

    [Fact]
    public void AfterInsertsRightAfterTheOneAnchor()
        => Assert.Equal("#version 450\nlayout(location=0) out vec4 o;\nlayout(location=1) out vec4 p;\nvoid main() {\n    o = vec4(1.0);\n}",
            ShaderText.After(Source, "layout(location=0) out vec4 o;", "\nlayout(location=1) out vec4 p;"));

    [Fact]
    public void AfterRefusesAMissingOrRepeatedAnchor()
    {
        Assert.Throws<InvalidOperationException>(() => ShaderText.After(Source, "no such line", "x"));
        Assert.Throws<InvalidOperationException>(() => ShaderText.After(Source + Source, "void main()", "x"));
    }

    [Fact]
    public void BeforeEndOfMainInsertsBeforeTheClosingBrace()
        => Assert.Equal("#version 450\nlayout(location=0) out vec4 o;\nvoid main() {\n    o = vec4(1.0);\n    o.w = 0.5;\n}",
            ShaderText.BeforeEndOfMain(Source, "    o.w = 0.5;\n"));

    [Fact]
    public void BeforeEndOfMainRefusesASourceThatDoesNotEndWithMain()
    {
        Assert.Throws<InvalidOperationException>(() => ShaderText.BeforeEndOfMain(Source + "\nfloat f() { return 1.0; }", "x"));
        Assert.Throws<InvalidOperationException>(() => ShaderText.BeforeEndOfMain("#version 450\n", "x"));
    }

    [Fact]
    public void BeforeEndOfMainSkipsBracesNestedInsideMain()
        => Assert.Equal("void main() {\n    if (true) { o = 1.0; }\n    o = 2.0;\n}",
            ShaderText.BeforeEndOfMain("void main() {\n    if (true) { o = 1.0; }\n}", "    o = 2.0;\n"));

    [Fact]
    public void AfterRefusesAnAnchorThatOverlapsItsOwnRepeat()
        => Assert.Throws<InvalidOperationException>(() => ShaderText.After("}}}", "}}", "x"));
}
