using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui.Chat;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// A chat box with a full scrollback used to slice every timestamped row into two new strings and measure the
/// stamp on every frame, for every row the scissor then discarded
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1116). A steady frame now sends the cached runs of the rows on
/// screen and allocates nothing. The rows go through the internal sink the real draw uses, because a sprite batch
/// needs a GPU device.
/// </summary>
[Collection("AllocSensitive")]
public sealed class ChatBoxDrawAllocationTests
{
    static readonly FixedMeasurer Font = new();

    [Fact]
    public void Drawing_a_steady_full_history_allocates_nothing()
    {
        var history = new ChatHistory(100);
        for (int i = 0; i < 100; i++)
        {
            string content = $"message {i}";
            history.Add(new ChatEntry(DateTimeOffset.UnixEpoch.AddMinutes(i), $"source-{i}",
                LocalizedText.Raw("Alice"), LocalizedText.Raw(content), content,
                i % 10 == 0 ? ChatEntryKind.System : ChatEntryKind.Ordinary, IsOwn: i % 3 == 0));
        }
        var box = new ChatBox(history, new Rect(100f, 100f, 300f, 180f));
        box.RefreshLayout(Font, TimeZoneInfo.Utc);

        // The first pass JITs the generic row loop for this sink. Measuring it would charge one-off runtime bytes
        // to the per-frame cost this test is about.
        var sink = new CountingSink();
        box.DrawHistoryRows(ref sink);
        Assert.True(sink.Runs > 0);

        AllocAssert.NoPerCallAllocation("ChatBox history rows on a steady frame", () =>
        {
            for (int frame = 0; frame < 60; frame++)
            {
                box.RefreshLayout(Font, TimeZoneInfo.Utc);
                box.DrawHistoryRows(ref sink);
            }
        });
    }

    struct CountingSink : IChatRowSink
    {
        public int Runs;

        public void DrawText(string text, Vector2 position, Color color) => Runs++;
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 16f;

        public Vector2 Measure(string text) => new(text.Length * 8f, LineHeight);
    }
}
