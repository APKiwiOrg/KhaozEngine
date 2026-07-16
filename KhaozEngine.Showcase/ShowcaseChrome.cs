using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Standard chrome every showcase room wears: a title, one or two controls-hint lines, an optional
    /// live status line, and access to transient toasts. A room implements this so the app-level
    /// <see cref="ShowcaseHud"/> can render one consistent frame (title band top-left, controls band bottom,
    /// status line beside the title, toasts centred) around whatever the room itself draws, instead of every room
    /// hand-rolling its own title / hint / HUD in a different place. The menu and the map editor are not
    /// <see cref="IShowcaseRoom"/>s: they carry their own chrome, so the hud skips them.</summary>
    public interface IShowcaseRoom
    {
        /// <summary>The room's display title (localized), shown top-left.</summary>
        StringId Title { get; }

        /// <summary>One or two localized controls-hint lines, shown in the bottom band above the display readout.</summary>
        IReadOnlyList<StringId> ControlsHints { get; }

        /// <summary>Optional live dev diagnostics drawn beside the title (raw text: net stats, skinning path).
        /// Null (the default) shows nothing, so a room opts in only when it has something to report.</summary>
        string? StatusLine => null;
    }

    /// <summary>Owns the toast state and draws all shared room chrome through the point-space UI pass, so its text
    /// stays crisp on HiDPI. One instance lives on <see cref="ShowcaseApp"/> and renders after each scene's own
    /// DrawUi (and before the display readout), reading the active scene as an <see cref="IShowcaseRoom"/>.</summary>
    public sealed class ShowcaseHud
    {
        const float ToastSeconds = 2.5f;
        // The toast sits just under the title band; the controls band steps at 20 points per hint line.
        const float ToastY = 52f;
        const float TitlePillHeight = 34f;
        const float HintLineStep = 20f;

        readonly Texture2D _white;
        readonly DpiFont _title;
        readonly DpiFont _body;

        string? _toast;
        float _toastRemaining;

        public ShowcaseHud(Texture2D white, DpiFont title, DpiFont body)
        {
            _white = white;
            _title = title;
            _body = body;
        }

        /// <summary>Show a transient message centred near the top for <see cref="ToastSeconds"/> (raw diagnostics
        /// text: a toggle just flipped). A new toast replaces any still-fading one.</summary>
        public void Toast(string message)
        {
            _toast = message;
            _toastRemaining = ToastSeconds;
        }

        /// <summary>Decay the active toast. Call once per frame.</summary>
        public void Update(float dt)
        {
            if (_toastRemaining <= 0f) return;
            _toastRemaining -= dt;
            if (_toastRemaining <= 0f) { _toastRemaining = 0f; _toast = null; }
        }

        // The chrome renderer is dev-facing composition: a room's Title/ControlsHints resolve through their
        // StringIds, and StatusLine + the toast are raw diagnostics, so the raw DrawString calls here are the
        // intentional localization escape hatch.
        [LocalizationExempt]
        public void Draw(SpriteBatch batch, UiViewport ui, IShowcaseRoom? room)
        {
            SpriteFont titleFont = _title.For(ui.DpiScale);
            SpriteFont bodyFont = _body.For(ui.DpiScale);
            GuiTheme theme = GuiTheme.Default;
            var pill = new Color(0f, 0f, 0f, 0.55f);

            if (room is not null)
            {
                // Title band: a translucent pill padded to the title width, top-left.
                string title = Resolve(room.Title);
                Vector2 ts = titleFont.Measure(title);
                float titlePillW = ts.X + 16f;
                batch.Draw(_white, new Vector4(8f, 6f, titlePillW, TitlePillHeight), pill);
                batch.DrawString(titleFont, title,
                    new Vector2(16f, 6f + (TitlePillHeight - titleFont.LineHeight) * 0.5f), (Color)theme.Text);

                // Status line (live dev diagnostics) in its own pill immediately right of the title, so Room3D's
                // skinning line and RoomNet's net stats live in one consistent spot.
                string? status = room.StatusLine;
                if (!string.IsNullOrEmpty(status))
                {
                    float sx = 8f + titlePillW + 8f;
                    Vector2 ss = bodyFont.Measure(status);
                    batch.Draw(_white, new Vector4(sx, 6f, ss.X + 16f, TitlePillHeight), pill);
                    batch.DrawString(bodyFont, status,
                        new Vector2(sx + 8f, 6f + (TitlePillHeight - bodyFont.LineHeight) * 0.5f), (Color)theme.TextMuted);
                }

                // Controls band: sits directly above the app's display readout band, one hint line per row.
                IReadOnlyList<StringId> hints = room.ControlsHints;
                if (hints.Count > 0)
                {
                    float bandH = 8f + HintLineStep * hints.Count;
                    float bandTop = ui.Height - ShowcaseApp.DisplayReadoutHeight - bandH;
                    batch.Draw(_white, new Vector4(0f, bandTop, ui.Width, bandH), pill);
                    var hintColor = new Color(0.85f, 0.95f, 1f, 1f);   // light blue-grey, matching the readout text
                    for (int i = 0; i < hints.Count; i++)
                        batch.DrawString(bodyFont, Resolve(hints[i]),
                            new Vector2(16f, bandTop + 4f + HintLineStep * i), hintColor);
                }
            }

            // Toast, independent of the room: centred horizontally near the top, fading with the remaining time.
            if (_toastRemaining > 0f && _toast is not null)
            {
                float a = MathF.Min(1f, _toastRemaining / ToastSeconds);
                Vector2 ms = titleFont.Measure(_toast);
                float x = (ui.Width - ms.X) * 0.5f;
                batch.Draw(_white, new Vector4(x - 12f, ToastY - 5f, ms.X + 24f, titleFont.LineHeight + 10f),
                    new Color(0f, 0f, 0f, 0.6f * a));
                batch.DrawString(titleFont, _toast, new Vector2(x, ToastY),
                    new Color(theme.Text.X, theme.Text.Y, theme.Text.Z, a));
            }
        }

        static string Resolve(StringId id) => ((LocalizedText)id).Resolve();
    }
}
