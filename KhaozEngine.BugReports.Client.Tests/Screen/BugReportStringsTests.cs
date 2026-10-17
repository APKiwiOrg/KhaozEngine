using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using KhaozEngine.App;
using KhaozEngine.BugReports.Client;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client.Screen;

[Collection("AmbientLocalization")]
public sealed class BugReportStringsTests
{
    /// <summary>The fixed English copy. A copy edit changes this table and the shipped defaults together.</summary>
    static readonly Dictionary<string, string> Table = new()
    {
        ["bugreport.screen.title"] = "Report a bug",
        ["bugreport.screen.description.label"] = "What happened?",
        ["bugreport.screen.description.placeholder"] = "Describe what you saw and what you expected to happen.",
        ["bugreport.screen.description.counter"] = "{0}/{1}",
        ["bugreport.screen.category.label"] = "Category",
        ["bugreport.screen.screenshot.toggle"] = "Include screenshot",
        ["bugreport.screen.clip.toggle"] = "Include the last few seconds of video",
        ["bugreport.screen.clip.unavailable"] = "Video is not available on this device",
        ["bugreport.screen.included"] = "Your account and your game and system info are included.",
        ["bugreport.screen.signed-out"] = "You are not signed in. This report will be sent after you sign in.",
        ["bugreport.screen.send"] = "Send",
        ["bugreport.screen.cancel"] = "Cancel",
        ["bugreport.screen.capturing"] = "Capturing...",
        ["bugreport.toast.sent"] = "Bug report sent. Reference {0}",
        ["bugreport.toast.queued"] = "Bug report saved. It will be sent the next time you sign in.",
        ["bugreport.toast.queued-signed-out"] = "Bug report saved. It will be sent after you sign in.",
        ["bugreport.toast.rejected"] = "Bug report not accepted ({0}).",
        ["bugreport.toast.failed"] = "The bug report could not be prepared.",
        ["bugreport.toast.flushed"] = "{0} saved bug reports sent.",
    };

    static readonly (string Id, string English)[] Categories =
    [
        ("Gameplay", "Gameplay"), ("Visual", "Visual"), ("Audio", "Audio"),
        ("Performance", "Performance"), ("Interface", "Interface"), ("Other", "Other"),
    ];

    static StringId[] DeclaredIds() => typeof(BugReportStrings)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(StringId) && f.IsInitOnly)
        .Select(f => (StringId)f.GetValue(null)!)
        .ToArray();

    [Fact]
    public void English_defaults_cover_every_key()
    {
        using var _ = LocalizationContext.Use(null);
        StringId[] ids = DeclaredIds();

        Assert.Equal(Table.Keys.OrderBy(k => k, System.StringComparer.Ordinal),
            ids.Select(id => id.Key).OrderBy(k => k, System.StringComparer.Ordinal));
        foreach (StringId id in ids)
        {
            Assert.Equal(Table[id.Key], BugReportStrings.Resolve(id));
            Assert.Equal(Table[id.Key], BugReportStrings.EnglishDefaults.Get(id.Key));
        }
        foreach ((string category, string english) in Categories)
        {
            Assert.Equal(english, BugReportStrings.CategoryLabel(category));
            Assert.Equal(english, BugReportStrings.EnglishDefaults.Get("bugreport.category." + category.ToLowerInvariant()));
        }
    }

    [Fact]
    public void Wired_catalog_overrides_a_line()
    {
        using var _ = LocalizationContext.Use(new Catalog()
            .Add("bugreport.screen.send", "Envoyer")
            .Add("bugreport.category.visual", "Graphismes"));

        Assert.Equal("Envoyer", BugReportStrings.Resolve(BugReportStrings.Send));
        Assert.Equal("Cancel", BugReportStrings.Resolve(BugReportStrings.Cancel));
        Assert.Equal("Graphismes", BugReportStrings.CategoryLabel("Visual"));
        Assert.Equal("Audio", BugReportStrings.CategoryLabel("Audio"));
    }

    [Fact]
    public void English_templates_take_the_documented_arguments()
    {
        var expected = new Dictionary<string, int>
        {
            ["bugreport.screen.description.counter"] = 2,
            ["bugreport.toast.sent"] = 1,
            ["bugreport.toast.rejected"] = 1,
            ["bugreport.toast.flushed"] = 1,
        };
        foreach (StringId id in DeclaredIds())
        {
            string template = BugReportStrings.EnglishDefaults.Get(id.Key);
            int arguments = Regex.Matches(template, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Count();
            Assert.True(arguments == expected.GetValueOrDefault(id.Key), $"{id.Key} takes {arguments} arguments");
        }

        using var _ = LocalizationContext.Use(null);
        Assert.Equal("12/4000", BugReportStrings.Format(BugReportStrings.DescriptionCounter, 12, 4000));
        Assert.Equal("Bug report sent. Reference R-7", BugReportStrings.Format(BugReportStrings.ToastSent, "R-7"));
        Assert.Equal("Bug report not accepted (too_large).", BugReportStrings.Format(BugReportStrings.ToastRejected, "too_large"));
        Assert.Equal("3 saved bug reports sent.", BugReportStrings.Format(BugReportStrings.ToastFlushed, 3));
    }

    [Fact]
    public void Unknown_category_falls_back_to_its_id()
    {
        using var _ = LocalizationContext.Use(new Catalog().Add("bugreport.screen.send", "Envoyer"));

        Assert.Equal("Crafting", BugReportStrings.CategoryLabel("Crafting"));
    }

    /// <summary>A minimal in-memory catalog that formats with the invariant culture.</summary>
    internal sealed class Catalog : IStringCatalog
    {
        readonly Dictionary<string, string> _map = new();

        public Catalog Add(string key, string value)
        {
            _map[key] = value;
            return this;
        }

        public string Get(string key) => _map.TryGetValue(key, out string? v) ? v : key;

        public string Format(string key, params object?[] args) =>
            IStringCatalog.SafeFormat(System.Globalization.CultureInfo.InvariantCulture, Get(key), args);

        public bool TryGet(string key, out string value)
        {
            value = Get(key);
            return _map.ContainsKey(key);
        }
    }
}
