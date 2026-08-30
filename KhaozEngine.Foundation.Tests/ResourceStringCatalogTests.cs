using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class ResourceStringCatalogTests
{
    [Fact]
    public void Get_PresentKey_ReturnsLocalizedValue()
    {
        var rm = new FakeStringResourceManager
        {
            ["en-US"] = { ["greeting"] = "Hello" },
        };
        IStringCatalog strings = new ResourceStringCatalog(rm);

        WithCulture("en-US", () => Assert.Equal("Hello", strings.Get("greeting")));
    }

    [Fact]
    public void Get_AbsentKey_ReturnsKeyItself_DoesNotThrow()
    {
        var rm = new FakeStringResourceManager();
        IStringCatalog strings = new ResourceStringCatalog(rm);

        WithCulture("en-US", () => Assert.Equal("missing.key", strings.Get("missing.key")));
    }

    [Fact]
    public void TryGet_PresentKey_ReturnsTrueAndValue()
    {
        var rm = new FakeStringResourceManager
        {
            ["en-US"] = { ["greeting"] = "Hello" },
        };
        IStringCatalog strings = new ResourceStringCatalog(rm);

        WithCulture("en-US", () =>
        {
            Assert.True(strings.TryGet("greeting", out string value));
            Assert.Equal("Hello", value);
        });
    }

    [Fact]
    public void TryGet_AbsentKey_ReturnsFalseAndValueIsKey()
    {
        var rm = new FakeStringResourceManager();
        IStringCatalog strings = new ResourceStringCatalog(rm);

        WithCulture("en-US", () =>
        {
            Assert.False(strings.TryGet("missing.key", out string value));
            Assert.Equal("missing.key", value);
        });
    }

    [Fact]
    public void TryGet_PresentKeyWhoseTranslationEqualsTheKey_ReturnsTrue()
    {
        var rm = new FakeStringResourceManager
        {
            // A real resx carries entries like this: an untranslated placeholder, or a culture whose
            // translation genuinely is the key text.
            ["en-US"] = { ["OK"] = "OK" },
        };
        IStringCatalog strings = new ResourceStringCatalog(rm);

        WithCulture("en-US", () =>
        {
            Assert.True(strings.TryGet("OK", out string value));
            Assert.Equal("OK", value);
        });
    }

    [Fact]
    public void Format_SubstitutesArgsIntoResolvedTemplate()
    {
        var rm = new FakeStringResourceManager
        {
            ["en-US"] = { ["welcome"] = "Welcome, {0}!" },
        };
        IStringCatalog strings = new ResourceStringCatalog(rm);

        WithCulture("en-US", () => Assert.Equal("Welcome, Ada!", strings.Format("welcome", "Ada")));
    }

    /// <summary>
    /// A translated value whose placeholder set diverges from the neutral template is a content defect in the
    /// resx, and it reached the engine as a FormatException out of the draw call that resolved the text (#163).
    /// Format falls back to the unformatted template: visibly wrong text, live process.
    /// </summary>
    [Fact]
    public void Format_TemplateTheArgsCannotSatisfy_FallsBackToTemplate_DoesNotThrow()
    {
        var rm = new FakeStringResourceManager
        {
            // The translator added a second placeholder the call site knows nothing about.
            ["en-US"] = { ["score"] = "Score: {0}, Bonus: {1}" },
        };
        IStringCatalog strings = new ResourceStringCatalog(rm);

        WithCulture("en-US", () => Assert.Equal("Score: {0}, Bonus: {1}", strings.Format("score", 7)));
    }

    [Fact]
    public void Get_ReadsCurrentUiCultureLive_AfterSetCulture()
    {
        var rm = new FakeStringResourceManager
        {
            ["en-US"] = { ["greeting"] = "Hello" },
            ["fr-FR"] = { ["greeting"] = "Bonjour" },
        };
        IStringCatalog strings = new ResourceStringCatalog(rm);

        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            LocalizationManager.SetCulture("en-US");
            Assert.Equal("Hello", strings.Get("greeting"));

            LocalizationManager.SetCulture("fr-FR");
            Assert.Equal("Bonjour", strings.Get("greeting"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void LocalizationManager_Catalog_ResolvesOverSameResourceManager()
    {
        var rm = new FakeStringResourceManager
        {
            ["en-US"] = { ["greeting"] = "Hello" },
        };
        var loc = new LocalizationManager(rm);
        IStringCatalog strings = loc.Catalog;

        WithCulture("en-US", () => Assert.Equal("Hello", strings.Get("greeting")));
    }

    /// <summary>Runs <paramref name="body"/> with the current + UI culture set, restoring both after.</summary>
    private static void WithCulture(string cultureCode, System.Action body)
    {
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            LocalizationManager.SetCulture(cultureCode);
            body();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
        }
    }
}

/// <summary>
/// Test double: a per-culture key/value store. <see cref="GetString(string, CultureInfo?)"/>
/// resolves against the culture's own table by <see cref="CultureInfo.Name"/>, returning null for
/// absent keys (as a real <see cref="ResourceManager"/> ultimately does when nothing resolves).
/// Enumerable/indexer initializer support lets tests seed cultures inline.
/// </summary>
internal sealed class FakeStringResourceManager : ResourceManager
{
    private readonly Dictionary<string, Dictionary<string, string>> _byCulture = new();

    public Dictionary<string, string> this[string cultureName]
    {
        get
        {
            if (!_byCulture.TryGetValue(cultureName, out Dictionary<string, string>? table))
            {
                table = new Dictionary<string, string>();
                _byCulture[cultureName] = table;
            }
            return table;
        }
    }

    public override string? GetString(string name, CultureInfo? culture)
    {
        culture ??= CultureInfo.CurrentUICulture;
        if (_byCulture.TryGetValue(culture.Name, out Dictionary<string, string>? table)
            && table.TryGetValue(name, out string? value))
        {
            return value;
        }
        return null;
    }
}
