using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui;

[Collection("AmbientLocalization")]
public sealed class InlineMarkupTests
{
    static readonly Vector4 Plain = new(0.8f, 0.8f, 0.8f, 1f);
    static readonly Vector4 Emphasis = new(1f, 0.7f, 0.2f, 1f);
    static readonly Vector4 Key = new(0.4f, 0.8f, 1f, 1f);
    static readonly InlineTextStyles Styles = new(
        new InlineTextStyle("emphasis", Emphasis),
        new InlineTextStyle("key", Key));

    [Fact]
    public void Nested_spans_follow_the_caller_style_map_and_restore_the_outer_colour()
    {
        SetCatalog("Tip.Action", "Use [emphasis]rare [key]E[/] item[/].", () =>
        {
            ColoredTextRun[] runs = InlineMarkup.Resolve(MarkupText.Of(new StringId("Tip.Action")), Styles, Plain);

            Assert.Equal(
            [
                new ColoredTextRun("Use ", Plain),
                new ColoredTextRun("rare ", Emphasis),
                new ColoredTextRun("E", Key),
                new ColoredTextRun(" item", Emphasis),
                new ColoredTextRun(".", Plain),
            ], runs);
        });
    }

    [Fact]
    public void Missing_style_mapping_inherits_the_enclosing_colour()
    {
        SetCatalog("Tip.Action", "[emphasis]rare [unmapped]item[/][/].", () =>
        {
            ColoredTextRun[] runs = InlineMarkup.Resolve(MarkupText.Of(new StringId("Tip.Action")), Styles, Plain);

            Assert.Equal(
            [
                new ColoredTextRun("rare item", Emphasis),
                new ColoredTextRun(".", Plain),
            ], runs);
        });
    }

    [Fact]
    public void Double_opening_bracket_emits_one_literal_bracket()
    {
        SetCatalog("Tip.Array", "Use [[0] or [key]E[/]", () =>
        {
            ColoredTextRun[] runs = InlineMarkup.Resolve(MarkupText.Of(new StringId("Tip.Array")), Styles, Plain);

            Assert.Equal("Use [0] or E", string.Concat(runs.Select(run => run.Text)));
            Assert.Equal(Key, runs[^1].Color);
        });
    }

    [Theory]
    [InlineData("Use [key]E")]
    [InlineData("Use E[/]")]
    [InlineData("Use [bad name]E[/]")]
    [InlineData("Use [key")]
    public void Malformed_markup_renders_the_complete_source_literally(string source)
    {
        SetCatalog("Tip.Bad", source, () =>
        {
            ColoredTextRun[] runs = InlineMarkup.Resolve(MarkupText.Of(new StringId("Tip.Bad")), Styles, Plain);

            Assert.Equal([new ColoredTextRun(source, Plain)], runs);
        });
    }

    [Fact]
    public void Formatted_arguments_are_escaped_before_the_trusted_template_is_parsed()
    {
        SetCatalog("Chat.Joined", "[emphasis]{0} joined[/]", () =>
        {
            MarkupText text = MarkupText.Of(new StringId("Chat.Joined"), "Ada[key]admin[/]");

            ColoredTextRun[] runs = InlineMarkup.Resolve(text, Styles, Plain);

            Assert.Equal([new ColoredTextRun("Ada[key]admin[/] joined", Emphasis)], runs);
        });
    }

    [Fact]
    public void Formatted_arguments_keep_the_catalog_placeholder_format()
    {
        SetCatalog("Chat.Count", "[key]{0:D4}[/]", () =>
        {
            ColoredTextRun[] runs = InlineMarkup.Resolve(
                MarkupText.Of(new StringId("Chat.Count"), 7), Styles, Plain);

            Assert.Equal([new ColoredTextRun("0007", Key)], runs);
        });
    }

    static void SetCatalog(string key, string value, System.Action assertion)
    {
        IStringCatalog? previous = LocalizationContext.Catalog;
        try
        {
            LocalizationContext.Catalog = new DictionaryCatalog().Add(key, value);
            assertion();
        }
        finally
        {
            LocalizationContext.Catalog = previous;
        }
    }
}
