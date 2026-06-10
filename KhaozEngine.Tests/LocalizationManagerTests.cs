using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;
using KhaozEngine.Localization;
using Xunit;

namespace KhaozEngine.Tests;

public class LocalizationManagerTests
{
    [Fact]
    public void DefaultCultureCode_IsEnUs()
    {
        Assert.Equal("en-US", LocalizationManager.DefaultCultureCode);
    }

    [Fact]
    public void SetCulture_ValidCode_SetsCurrentAndUiCulture()
    {
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            LocalizationManager.SetCulture("fr-FR");

            Assert.Equal("fr-FR", Thread.CurrentThread.CurrentCulture.Name);
            Assert.Equal("fr-FR", Thread.CurrentThread.CurrentUICulture.Name);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void SetCulture_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LocalizationManager.SetCulture(null!));
    }

    [Fact]
    public void SetCulture_Empty_Throws()
    {
        // Assert on the base ArgumentException so the subtype is not over-specified.
        // xUnit 2.9.2 uses exact matching in Assert.Throws<T>, so we use ThrowsAny<T> here.
        Assert.ThrowsAny<ArgumentException>(() => LocalizationManager.SetCulture(""));
    }

    [Fact]
    public void GetSupportedCultures_ReturnsCulturesWithResourceSets_PlusInvariant()
    {
        var rm = new FakeResourceManager(
            supported: new HashSet<string> { "fr-FR", "es-ES" },
            throwing: new HashSet<string> { "de-DE" });
        var manager = new LocalizationManager(rm);

        List<CultureInfo> result = manager.GetSupportedCultures();

        Assert.Contains(result, c => c.Name == "fr-FR");
        Assert.Contains(result, c => c.Name == "es-ES");
        Assert.Contains(result, c => c.Equals(CultureInfo.InvariantCulture));
        Assert.DoesNotContain(result, c => c.Name == "de-DE");
    }

    [Fact]
    public void GetSupportedCultures_NoResourceSets_ReturnsOnlyInvariant()
    {
        var rm = new FakeResourceManager(
            supported: new HashSet<string>(),
            throwing: new HashSet<string>());
        var manager = new LocalizationManager(rm);

        List<CultureInfo> result = manager.GetSupportedCultures();

        Assert.Single(result);
        Assert.Equal(CultureInfo.InvariantCulture, result[0]);
    }

    [Fact]
    public void Ctor_NullResourceManager_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalizationManager(null!));
    }
}

/// <summary>A non-null sentinel ResourceSet; never read, only checked for non-null.</summary>
internal sealed class FakeResourceSet : ResourceSet
{
    // Uses ResourceSet's protected parameterless ctor.
}

/// <summary>
/// Test double: returns a sentinel resource set for "supported" cultures, throws
/// MissingManifestResourceException for "throwing" cultures, and null for everything else.
/// </summary>
internal sealed class FakeResourceManager : ResourceManager
{
    private readonly HashSet<string> _supported;
    private readonly HashSet<string> _throwing;

    public FakeResourceManager(HashSet<string> supported, HashSet<string> throwing)
    {
        _supported = supported;
        _throwing = throwing;
    }

    public override ResourceSet? GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
    {
        if (_throwing.Contains(culture.Name))
            throw new MissingManifestResourceException($"no resources for {culture.Name}");
        if (_supported.Contains(culture.Name))
            return new FakeResourceSet();
        return null;
    }
}
