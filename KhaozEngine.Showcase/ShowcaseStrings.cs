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
        public static readonly StringId MenuPatchNotes = new("Menu.PatchNotes");
        public static readonly StringId MenuToasts = new("Menu.Toasts");
        public static readonly StringId MenuFooter = new("Menu.Footer");

        // Settings
        public static readonly StringId SettingsTitle = new("Settings.Title");
        public static readonly StringId SettingsVolume = new("Settings.Volume");
        public static readonly StringId SettingsFullscreen = new("Settings.Fullscreen");
        public static readonly StringId SettingsHelp = new("Settings.Help");

        // Widgets
        public static readonly StringId WidgetsTitle = new("Widgets.Title");
        public static readonly StringId WidgetsName = new("Widgets.Name");
        public static readonly StringId WidgetsNamePlaceholder = new("Widgets.NamePlaceholder");
        public static readonly StringId WidgetsDifficulty = new("Widgets.Difficulty");
        public static readonly StringId WidgetsList = new("Widgets.List");
        public static readonly StringId WidgetsHoverForTip = new("Widgets.HoverForTip");
        public static readonly StringId WidgetsConfirm = new("Widgets.Confirm");
        public static readonly StringId WidgetsTipTitle = new("Widgets.TipTitle");
        public static readonly StringId WidgetsTipLine1 = new("Widgets.TipLine1");
        public static readonly StringId WidgetsTipLine2 = new("Widgets.TipLine2");
        public static readonly StringId WidgetsHotbar = new("Widgets.Hotbar");
        public static readonly StringId WidgetsLoading = new("Widgets.Loading");
        public static readonly StringId WidgetsSkinTitle = new("Widgets.SkinTitle");
        public static readonly StringId WidgetsSkinButton = new("Widgets.SkinButton");
        public static readonly StringId WidgetsSkinPanel = new("Widgets.SkinPanel");

        // Overlay demo
        public static readonly StringId OverlayTitle = new("Overlay.Title");
        public static readonly StringId OverlayHint = new("Overlay.Hint");
        public static readonly StringId OverlayPush = new("Overlay.Push");
        public static readonly StringId OverlayPaused = new("Overlay.Paused");
        public static readonly StringId OverlayResume = new("Overlay.Resume");

        // Toasts demo
        public static readonly StringId ToastsTitle = new("Toasts.Title");
        public static readonly StringId ToastsStandard = new("Toasts.Standard");
        public static readonly StringId ToastsWarning = new("Toasts.Warning");
        public static readonly StringId ToastsDanger = new("Toasts.Danger");
        public static readonly StringId ToastsSticky = new("Toasts.Sticky");
        public static readonly StringId ToastsUpdate = new("Toasts.Update");
        public static readonly StringId ToastsClear = new("Toasts.Clear");
        public static readonly StringId ToastsStandardMessage = new("Toasts.StandardMessage");
        public static readonly StringId ToastsWarningMessage = new("Toasts.WarningMessage");
        public static readonly StringId ToastsDangerMessage = new("Toasts.DangerMessage");
        public static readonly StringId ToastsStickyMessage = new("Toasts.StickyMessage");
        public static readonly StringId ToastsCounterMessage = new("Toasts.CounterMessage");

        // Mini-game
        public static readonly StringId MiniGamePlay = new("MiniGame.Play");
        public static readonly StringId MiniGameBackToMenu = new("MiniGame.BackToMenu");
        public static readonly StringId MiniGameRetry = new("MiniGame.Retry");

        // Popup (PopupPanel demo)
        public static readonly StringId PopupTitle = new("Popup.Title");
        public static readonly StringId PopupCancel = new("Popup.Cancel");
        public static readonly StringId PopupStart = new("Popup.Start");
        public static readonly StringId PopupSummary = new("Popup.Summary");
        public static readonly StringId PopupName = new("Popup.Name");
        public static readonly StringId PopupDifficulty = new("Popup.Difficulty");
        public static readonly StringId PopupUnnamed = new("Popup.Unnamed");
        public static readonly StringId PopupNote = new("Popup.Note");
        public static readonly StringId PopupNoteBody = new("Popup.NoteBody");

        // Shared
        public static readonly StringId CommonBack = new("Common.Back");

        // Boot screen demo
        public static readonly StringId BootStepAssets = new("Boot.StepAssets");
        public static readonly StringId BootStepAudio = new("Boot.StepAudio");
        public static readonly StringId BootStepWorld = new("Boot.StepWorld");
        public static readonly StringId BootHint = new("Boot.Hint");
    }
}
