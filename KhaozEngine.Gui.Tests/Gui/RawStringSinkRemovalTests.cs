using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Pins the removal of the raw-string Gui sinks. Each player-facing text member takes only a
    /// <see cref="KhaozEngine.App.LocalizedText"/>, so a bare string at the sink is a compile error rather
    /// than a warning. A reintroduced <c>string</c> overload or <c>string</c> property fails here.
    /// </summary>
    public class RawStringSinkRemovalTests
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        [Theory]
        [InlineData(typeof(Button), ".ctor")]
        [InlineData(typeof(Label), ".ctor")]
        [InlineData(typeof(DropdownOption), ".ctor")]
        [InlineData(typeof(Tooltip), nameof(Tooltip.Show))]
        [InlineData(typeof(PopupRow), nameof(PopupRow.Header))]
        [InlineData(typeof(PopupRow), nameof(PopupRow.Stat))]
        [InlineData(typeof(ScrollablePanel), nameof(ScrollablePanel.DrawHeader))]
        [InlineData(typeof(GuiSurface), nameof(GuiSurface.Label))]
        [InlineData(typeof(GuiSurface), nameof(GuiSurface.Button))]
        [InlineData(typeof(GuiSurface), nameof(GuiSurface.StatChip))]
        public void Text_sink_has_no_raw_string_overload(Type type, string member)
        {
            MethodBase[] overloads = member == ".ctor"
                ? type.GetConstructors(Public)
                : type.GetMethods(Public).Where(m => m.Name == member).ToArray();
            Assert.NotEmpty(overloads);

            // StatChip keeps its non-localizable icon id as a string, so it is exempt by parameter name.
            MethodBase[] raw = overloads
                .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string) && p.Name != "iconId"))
                .ToArray();
            Assert.Empty(raw);
        }

        [Theory]
        [InlineData(typeof(Button), "Label")]
        [InlineData(typeof(Label), "Text")]
        [InlineData(typeof(TextInput), "Placeholder")]
        [InlineData(typeof(PopupPanel), "Title")]
        [InlineData(typeof(PopupPanel), "DismissText")]
        [InlineData(typeof(PopupPanel), "PrimaryActionText")]
        [InlineData(typeof(DropdownOption), "Label")]
        public void Raw_string_text_member_is_gone(Type type, string member)
        {
            Assert.Null(type.GetProperty(member, Public));
            Assert.Null(type.GetField(member, Public));
        }
    }
}
