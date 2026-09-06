using KhaozEngine.App;

namespace KhaozEngine.Gui
{
    /// <summary>One <see cref="ChoiceRow"/> option with independently localized display content and a stable
    /// string value for the row's get and set delegates.</summary>
    public readonly record struct ChoiceOption(LocalizedText Content, string Value);
}
