using KhaozEngine.App;

namespace KhaozEngine.Showcase
{
    /// <summary>
    /// Hand-authored localization keys for the showcase, mirroring <c>ShowcaseStrings.resx</c>. This is the
    /// consumer pattern until the engine ships a resx-to-StringId source generator (see the ROADMAP). Each
    /// constant is a <see cref="StringId"/> passed to a Gui sink; <see cref="LocalizedText"/> resolves it against
    /// the catalog registered into <see cref="LocalizationContext.Catalog"/> at startup.
    /// </summary>
    internal static class ShowcaseStrings
    {
        // Menu
        public static readonly StringId GuiTitle = new("Gui.Title");
        public static readonly StringId MenuSettings = new("Menu.Settings");
        public static readonly StringId MenuWidgets = new("Menu.Widgets");
        public static readonly StringId MenuImmediate = new("Menu.Immediate");
        public static readonly StringId MenuOverlayDemo = new("Menu.OverlayDemo");
        public static readonly StringId MenuFooter = new("Menu.Footer");

        // Settings
        public static readonly StringId SettingsTitle = new("Settings.Title");
        public static readonly StringId SettingsVolume = new("Settings.Volume");
        public static readonly StringId SettingsFullscreen = new("Settings.Fullscreen");
        public static readonly StringId SettingsHelp = new("Settings.Help");

        // Widgets
        public static readonly StringId WidgetsTitle = new("Widgets.Title");
        public static readonly StringId WidgetsName = new("Widgets.Name");
        public static readonly StringId WidgetsDifficulty = new("Widgets.Difficulty");
        public static readonly StringId WidgetsList = new("Widgets.List");
        public static readonly StringId WidgetsHoverForTip = new("Widgets.HoverForTip");
        public static readonly StringId WidgetsConfirm = new("Widgets.Confirm");
        public static readonly StringId WidgetsTipTitle = new("Widgets.TipTitle");
        public static readonly StringId WidgetsTipLine1 = new("Widgets.TipLine1");
        public static readonly StringId WidgetsTipLine2 = new("Widgets.TipLine2");

        // Overlay demo
        public static readonly StringId OverlayTitle = new("Overlay.Title");
        public static readonly StringId OverlayHint = new("Overlay.Hint");
        public static readonly StringId OverlayPush = new("Overlay.Push");
        public static readonly StringId OverlayPaused = new("Overlay.Paused");
        public static readonly StringId OverlayResume = new("Overlay.Resume");

        // Mini-game
        public static readonly StringId MiniGamePlay = new("MiniGame.Play");
        public static readonly StringId MiniGameBackToMenu = new("MiniGame.BackToMenu");
        public static readonly StringId MiniGameRetry = new("MiniGame.Retry");

        // Shared
        public static readonly StringId CommonBack = new("Common.Back");
    }
}
