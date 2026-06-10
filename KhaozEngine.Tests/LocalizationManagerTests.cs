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
        Assert.Equal("en-US", LocalizationManager.DEFAULT_CULTURE_CODE);
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
}
