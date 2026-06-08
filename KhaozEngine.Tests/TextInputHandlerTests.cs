using System;
using System.Collections.Generic;
using KhaozEngine.Input;
using KhaozEngine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;

namespace KhaozEngine.Tests;

public class TextInputHandlerTests
{
    private static readonly IReadOnlyList<GamePadState> NoPads =
        new[] { new GamePadState(), new GamePadState(), new GamePadState(), new GamePadState() };

    private static RawInputState Keys_(params Keys[] keys) =>
        new(Point.Zero, false, false, false, 0,
            new KeyboardState(keys), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    [Fact]
    public void TypingAppendsValidatedCharacters()
    {
        var im = new InputManager();
        var handler = new TextInputHandler(maxLength: 8, charValidator: c => char.IsLetterOrDigit(c));

        im.Update(Keys_(), true);                 // baseline
        im.Update(Keys_(Keys.A), true);           // press A
        handler.ProcessInput(im, null);

        Assert.Equal("a", handler.Text);          // A-Z map to lowercase, per the original handler
    }

    [Fact]
    public void BackspaceRemovesLastCharacter()
    {
        var im = new InputManager();
        var handler = new TextInputHandler(maxLength: 8, charValidator: c => true) { Text = "AB" };

        im.Update(Keys_(), true);
        im.Update(Keys_(Keys.Back), true);
        handler.ProcessInput(im, null);

        Assert.Equal("A", handler.Text);
    }
}
