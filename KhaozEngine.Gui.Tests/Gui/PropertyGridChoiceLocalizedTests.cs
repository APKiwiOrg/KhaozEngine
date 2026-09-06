using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class PropertyGridChoiceLocalizedTests
    {
        static readonly Rect Area = new(0, 0, 300, 150);
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 position, bool leftDown)
        {
            var held = new HashSet<MouseButton>();
            if (leftDown) held.Add(MouseButton.Left);
            var (pressed, released) = _mouse.Advance(held);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, 960, 540, mouseReleased: released);
        }

        void Step(InputManager input, PropertyGrid grid, Vector2 position, bool leftDown)
        {
            input.Update(Frame(position, leftDown));
            grid.Update(input, 0f);
        }

        void Tap(InputManager input, PropertyGrid grid, Vector2 position)
        {
            Step(input, grid, position, false);
            Step(input, grid, position, true);
            Step(input, grid, position, false);
        }

        [Fact]
        public void Localized_choice_round_trips_stable_value_and_refreshes_display_after_language_switch()
        {
            var english = new DictionaryCatalog()
                .Add("Difficulty.Easy", "Easy")
                .Add("Difficulty.Hard", "Hard");
            var french = new DictionaryCatalog()
                .Add("Difficulty.Easy", "Facile")
                .Add("Difficulty.Hard", "Difficile");
            LocalizationContext.Catalog = english;
            try
            {
                string value = "easy";
                var options = new[]
                {
                    new ChoiceOption(new StringId("Difficulty.Easy"), "easy"),
                    new ChoiceOption(new StringId("Difficulty.Hard"), "hard"),
                };
                var row = new ChoiceRow(LocalizedText.Raw("Difficulty"), options, () => value, v => value = v);
                var grid = new PropertyGrid(Area);
                grid.Rows.Add(row);
                var input = new InputManager();

                Assert.Equal("Easy", row.Selected);
                Tap(input, grid, new Vector2(200, 14));
                Tap(input, grid, new Vector2(200, 70));

                Assert.Equal("hard", value);
                Assert.Equal("Hard", row.Selected);

                LocalizationContext.Catalog = french;
                Assert.Equal("Difficile", row.Selected);

                value = "easy";
                Step(input, grid, new Vector2(400, 300), false);
                Assert.Equal("Facile", row.Selected);
                Assert.Equal("easy", value);
            }
            finally
            {
                LocalizationContext.Catalog = null;
            }
        }

        [Fact]
        public void Raw_string_choice_keeps_label_as_its_round_trip_value()
        {
            string value = "rect";
            var row = new ChoiceRow(LocalizedText.Raw("Kind"), new[] { "disc", "rect" }, () => value, v => value = v);

            Assert.Equal("rect", row.Selected);
            Assert.Equal("rect", row.Dropdown.SelectedContent.Resolve());
        }
    }
}
