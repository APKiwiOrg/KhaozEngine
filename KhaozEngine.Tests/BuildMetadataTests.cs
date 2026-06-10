using System;
using System.Reflection;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class BuildMetadataTests
{
    private const string PresentKey = "KhaozEngine.Tests.BuildMetadata.Present";
    private const string BlankKey = "KhaozEngine.Tests.BuildMetadata.Blank";

    private static Assembly TestAssembly => typeof(BuildMetadataTests).Assembly;

    [Fact]
    public void Read_KeyPresent_ReturnsValue()
    {
        Assert.Equal("present-value", BuildMetadata.Read(PresentKey, "fallback", TestAssembly));
    }

    [Fact]
    public void Read_KeyAbsent_ReturnsFallback()
    {
        Assert.Equal("fallback", BuildMetadata.Read("no.such.key", "fallback", TestAssembly));
    }

    [Fact]
    public void Read_FallsThroughMissingAssemblyToLaterAssembly()
    {
        // First assembly (corelib) lacks the key; second (test asm) has it.
        Assert.Equal(
            "present-value",
            BuildMetadata.Read(PresentKey, "fallback", typeof(object).Assembly, TestAssembly));
    }

    [Fact]
    public void Read_NullAssembly_IsSkipped()
    {
        Assert.Equal("present-value", BuildMetadata.Read(PresentKey, "fallback", null, TestAssembly));
    }

    [Fact]
    public void Read_WhitespaceValue_ReturnsFallback()
    {
        Assert.Equal("fallback", BuildMetadata.Read(BlankKey, "fallback", TestAssembly));
    }

    [Fact]
    public void Read_NoAssemblies_ReturnsFallback()
    {
        Assert.Equal("fallback", BuildMetadata.Read("any.key", "fallback"));
    }

    [Fact]
    public void Read_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BuildMetadata.Read(null!, "fallback", TestAssembly));
    }
}
