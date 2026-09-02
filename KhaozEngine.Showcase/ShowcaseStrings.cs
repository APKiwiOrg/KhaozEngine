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
        // Hub (the tile menu)
        public static readonly StringId HubTitle = new("Hub.Title");
        public static readonly StringId HubSubtitle = new("Hub.Subtitle");
        public static readonly StringId HubHint = new("Hub.Hint");
        public static readonly StringId HubEngineVersion = new("Hub.EngineVersion");

        // Room tiles: one Title + one Blurb per registered room, in menu order.
        public static readonly StringId RoomGui2DTitle = new("Room.Gui2D.Title");
        public static readonly StringId RoomGui2DBlurb = new("Room.Gui2D.Blurb");
        public static readonly StringId RoomMiniGameTitle = new("Room.MiniGame.Title");
        public static readonly StringId RoomMiniGameBlurb = new("Room.MiniGame.Blurb");
        public static readonly StringId RoomBootTitle = new("Room.Boot.Title");
        public static readonly StringId RoomBootBlurb = new("Room.Boot.Blurb");
        public static readonly StringId RoomWorldTitle = new("Room.World.Title");
        public static readonly StringId RoomWorldBlurb = new("Room.World.Blurb");
        public static readonly StringId RoomVfxTitle = new("Room.Vfx.Title");
        public static readonly StringId RoomVfxBlurb = new("Room.Vfx.Blurb");
        public static readonly StringId RoomNetTitle = new("Room.Net.Title");
        public static readonly StringId RoomNetBlurb = new("Room.Net.Blurb");
        public static readonly StringId RoomDungeonTitle = new("Room.Dungeon.Title");
        public static readonly StringId RoomDungeonBlurb = new("Room.Dungeon.Blurb");
        public static readonly StringId RoomMapEditorTitle = new("Room.MapEditor.Title");
        public static readonly StringId RoomMapEditorBlurb = new("Room.MapEditor.Blurb");

        // Per-room controls hints (the chrome's bottom band).
        public static readonly StringId ControlsGui2D = new("Controls.Gui2D");
        public static readonly StringId ControlsMiniGame = new("Controls.MiniGame");
        public static readonly StringId ControlsBoot = new("Controls.Boot");
        public static readonly StringId ControlsWorld1 = new("Controls.World1");
        public static readonly StringId ControlsWorld2 = new("Controls.World2");
        public static readonly StringId ControlsVfx = new("Controls.Vfx");
        public static readonly StringId ControlsNet = new("Controls.Net");
        public static readonly StringId ControlsDungeon = new("Controls.Dungeon");

        // 2D and GUI room: tab labels.
        public static readonly StringId TabWidgets = new("Tab.Widgets");
        public static readonly StringId TabSprites = new("Tab.Sprites");
        public static readonly StringId TabInput = new("Tab.Input");
        public static readonly StringId TabImmediate = new("Tab.Immediate");
        public static readonly StringId TabScreens = new("Tab.Screens");

        // Settings dialog
        public static readonly StringId SettingsTitle = new("Settings.Title");
        public static readonly StringId SettingsVolume = new("Settings.Volume");
        public static readonly StringId SettingsFullscreen = new("Settings.Fullscreen");
        public static readonly StringId SettingsHelp = new("Settings.Help");

        // Widgets page
        public static readonly StringId WidgetsSectionForm = new("Widgets.SectionForm");
        public static readonly StringId WidgetsSectionHud = new("Widgets.SectionHud");
        public static readonly StringId WidgetsSectionSkin = new("Widgets.SectionSkin");
        public static readonly StringId WidgetsName = new("Widgets.Name");
        public static readonly StringId WidgetsNamePlaceholder = new("Widgets.NamePlaceholder");
        public static readonly StringId WidgetsDifficulty = new("Widgets.Difficulty");
        public static readonly StringId WidgetsDifficultyEasy = new("Widgets.DifficultyEasy");
        public static readonly StringId WidgetsDifficultyNormal = new("Widgets.DifficultyNormal");
        public static readonly StringId WidgetsDifficultyHard = new("Widgets.DifficultyHard");
        public static readonly StringId WidgetsPartySize = new("Widgets.PartySize");
        public static readonly StringId WidgetsList = new("Widgets.List");
        public static readonly StringId WidgetsListItem = new("Widgets.ListItem");
        public static readonly StringId WidgetsHoverForTip = new("Widgets.HoverForTip");
        public static readonly StringId WidgetsConfirm = new("Widgets.Confirm");
        public static readonly StringId WidgetsTipTitle = new("Widgets.TipTitle");
        public static readonly StringId WidgetsTipLine1 = new("Widgets.TipLine1");
        public static readonly StringId WidgetsTipLine2 = new("Widgets.TipLine2");
        public static readonly StringId WidgetsHotbar = new("Widgets.Hotbar");
        public static readonly StringId WidgetsLoading = new("Widgets.Loading");
        public static readonly StringId WidgetsCastBar = new("Widgets.CastBar");
        public static readonly StringId WidgetsPips = new("Widgets.Pips");
        public static readonly StringId WidgetsResource = new("Widgets.Resource");
        public static readonly StringId WidgetsSkinButton = new("Widgets.SkinButton");
        public static readonly StringId WidgetsSkinPanel = new("Widgets.SkinPanel");

        // Sprites and text page
        public static readonly StringId SpritesSectionSprites = new("Sprites.SectionSprites");
        public static readonly StringId SpritesSectionText = new("Sprites.SectionText");
        public static readonly StringId SpritesCaptionScale = new("Sprites.CaptionScale");
        public static readonly StringId SpritesCaptionTint = new("Sprites.CaptionTint");
        public static readonly StringId SpritesCaptionAlpha = new("Sprites.CaptionAlpha");

        // Input and audio page: static row labels (values are raw diagnostics).
        public static readonly StringId InputGesture = new("Input.Gesture");
        public static readonly StringId InputClock = new("Input.Clock");
        public static readonly StringId InputSimTime = new("Input.SimTime");
        public static readonly StringId InputLastSfx = new("Input.LastSfx");
        public static readonly StringId InputGamepad = new("Input.Gamepad");
        public static readonly StringId InputClipboard = new("Input.Clipboard");
        public static readonly StringId InputKeys = new("Input.Keys");

        // Screens and dialogs page
        public static readonly StringId ScreensIntro = new("Screens.Intro");
        public static readonly StringId ScreensSettings = new("Screens.Settings");
        public static readonly StringId ScreensSettingsCaption = new("Screens.SettingsCaption");
        public static readonly StringId ScreensOverlay = new("Screens.Overlay");
        public static readonly StringId ScreensOverlayCaption = new("Screens.OverlayCaption");
        public static readonly StringId ScreensPatchNotes = new("Screens.PatchNotes");
        public static readonly StringId ScreensPatchNotesCaption = new("Screens.PatchNotesCaption");
        public static readonly StringId ScreensToasts = new("Screens.Toasts");
        public static readonly StringId ScreensToastsCaption = new("Screens.ToastsCaption");

        // Toasts demo (pushed from the Screens page)
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

        // Overlay demo (the pause overlay pushed from the Screens page)
        public static readonly StringId OverlayPaused = new("Overlay.Paused");
        public static readonly StringId OverlayResume = new("Overlay.Resume");

        // Mini-game
        public static readonly StringId MiniGameTitle = new("MiniGame.Title");
        public static readonly StringId MiniGameTagline = new("MiniGame.Tagline");
        public static readonly StringId MiniGameScore = new("MiniGame.Score");
        public static readonly StringId MiniGameLives = new("MiniGame.Lives");
        public static readonly StringId MiniGameFinalScore = new("MiniGame.FinalScore");
        public static readonly StringId MiniGameGameOver = new("MiniGame.GameOver");
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
    }
}
