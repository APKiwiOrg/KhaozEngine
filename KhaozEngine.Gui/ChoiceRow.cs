using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Choice property backed by get and set delegates over a stable string value, edited with a
    /// <see cref="Gui.Dropdown"/>. Display content can resolve through localization independently of that value.
    /// The external value is polled in only while the list is closed, so an in-progress pick is never stomped, and
    /// the setter fires only on a real change.
    /// </summary>
    public sealed class ChoiceRow : PropertyRow
    {
        readonly Func<string> _get;
        readonly Action<string> _set;
        readonly List<ChoiceOption> _options;
        Pointer? _pointer;

        /// <summary>The selector, exposed for style and inspection. Its trigger bounds are driven by the grid each frame.</summary>
        public Dropdown Dropdown { get; }

        /// <summary>The selected option's display text, resolved against the ambient localization catalog.</summary>
        public string Selected => Dropdown.SelectedLabel;

        /// <summary>
        /// Build a choice row whose raw option strings serve as both display text and round-trip values. This keeps
        /// the original API behavior. Use the <see cref="ChoiceOption"/> overload to localize display content.
        /// </summary>
        public ChoiceRow(LocalizedText label, IReadOnlyList<string> options, Func<string> get, Action<string> set,
            LocalizedText? description = null)
            : this(label, RawOptions(options), get, set, description)
        {
        }

        /// <summary>
        /// Build a choice row with localized display content and stable string values. <paramref name="options"/>
        /// must be non-empty. The initial selection matches <paramref name="get"/> by value, or stays on the first
        /// option when the getter value is unknown. <paramref name="description"/> is an optional tooltip.
        /// </summary>
        public ChoiceRow(LocalizedText label, IReadOnlyList<ChoiceOption> options, Func<string> get,
            Action<string> set, LocalizedText? description = null)
            : base(label, description)
        {
            _get = get;
            _set = set;
            _options = new List<ChoiceOption>(options);
            var dropdownOptions = new List<DropdownOption>(options.Count);
            for (int i = 0; i < options.Count; i++)
                dropdownOptions.Add(new DropdownOption(options[i].Content, i));
            Dropdown = new Dropdown(dropdownOptions, default) { ShowChevron = true };
            SelectOption(get());
        }

        /// <inheritdoc/>
        public override bool Update(Rect editorRect, InputManager input, float dt)
        {
            Dropdown.TriggerBounds = editorRect;
            _pointer = input.Pointer;
            if (!Dropdown.IsOpen) SelectOption(_get());

            bool changed = Dropdown.Update(input.Pointer);
            string selectedValue = _options[Dropdown.SelectedIndex].Value;
            if (changed && selectedValue != _get())
            {
                _set(selectedValue);
                return true;
            }
            return false;
        }

        /// <inheritdoc/>
        public override void Deactivate() => Dropdown.Close();

        /// <inheritdoc/>
        public override bool HasActiveEditor => Dropdown.IsOpen;

        /// <inheritdoc/>
        public override void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Rect editorRect)
        {
            Dropdown.TriggerBounds = editorRect;
            Dropdown.Draw(batch, white, font);
        }

        /// <inheritdoc/>
        public override void DrawOverlay(SpriteBatch batch, Texture2D white, SpriteFont font, Rect editorRect)
        {
            Dropdown.TriggerBounds = editorRect;
            if (_pointer != null) Dropdown.DrawOverlay(batch, white, font, _pointer);
        }

        internal override void ApplyOpacity(float opacity) => Dropdown.Opacity = opacity;

        internal override void ApplyEditorStyle(GuiStyle style) => Dropdown.Style = style;

        void SelectOption(string value)
        {
            for (int i = 0; i < _options.Count; i++)
                if (_options[i].Value == value) { Dropdown.SelectByValue(i); return; }
        }

        static IReadOnlyList<ChoiceOption> RawOptions(IReadOnlyList<string> options)
        {
            var result = new ChoiceOption[options.Count];
            for (int i = 0; i < options.Count; i++)
                result[i] = new ChoiceOption(LocalizedText.Raw(options[i]), options[i]);
            return result;
        }
    }
}
