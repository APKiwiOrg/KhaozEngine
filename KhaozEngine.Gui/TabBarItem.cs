using KhaozEngine.App;

namespace KhaozEngine.Gui
{
    /// <summary>One <see cref="TabBar"/> tab with localized label content and an input-enabled flag.</summary>
    public readonly record struct TabBarItem(LocalizedText Label, bool Enabled = true);
}
