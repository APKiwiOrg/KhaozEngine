using System;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Automation;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Frame-boundary semantics against a fake frame pump: no window, no socket, just <c>Submit</c> from the caller and
/// <c>Pump</c> standing in for the window thread. Every reply names the frame the command took effect on, which is
/// the whole point of the protocol.
/// </summary>
public class AutomationFramePumpTests
{
    static AutomationHost NewHost() => new(AutomationOptions.Off);

    static readonly InputState Frame = AutomationTestKit.Real(position: new Vector2(1, 2));

    [Fact]
    public async Task AQueuedInputTakesEffectOnTheNextFrame()
    {
        using AutomationHost host = NewHost();
        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse(
            "{\"id\":1,\"cmd\":\"input\",\"x\":400,\"y\":300,\"button\":\"left\"}"));

        Assert.False(reply.IsCompleted);
        Assert.Equal(0, host.Frame);

        InputState composed = host.Pump(Frame);

        Assert.Equal(new Vector2(400, 300), composed.MousePosition);
        Assert.Contains(MouseButton.Left, composed.MousePressed);

        AutomationReply result = await reply;
        Assert.Equal(1, result.Frame);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AnInputWithHoldFramesReleasesOnTheRightFrame()
    {
        using AutomationHost host = NewHost();
        host.Submit(AutomationTestKit.Parse("{\"id\":1,\"cmd\":\"input\",\"key\":\"W\",\"holdFrames\":2}"));

        Assert.True(host.Pump(Frame).IsDown(Key.W));            // frame 1, the press
        Assert.True(host.Pump(Frame).IsDown(Key.W));            // frame 2, still held

        InputState third = host.Pump(Frame);                    // frame 3, the auto-release
        Assert.False(third.IsDown(Key.W));
        Assert.True(third.WasReleased(Key.W));
    }

    [Fact]
    public async Task StepRepliesAfterExactlyThatManyPumpsWithThatFrameNumber()
    {
        using AutomationHost host = NewHost();
        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":9,\"cmd\":\"step\",\"frames\":3}"));

        host.Pump(Frame);
        Assert.False(reply.IsCompleted);
        host.Pump(Frame);
        Assert.False(reply.IsCompleted);
        host.Pump(Frame);

        Assert.True(reply.IsCompleted);
        AutomationReply result = await reply;
        Assert.Equal(3, result.Frame);
        Assert.Equal(9, result.Id);
    }

    [Fact]
    public async Task StepDefaultsToOneFrame()
    {
        using AutomationHost host = NewHost();
        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":1,\"cmd\":\"step\"}"));

        host.Pump(Frame);

        Assert.True(reply.IsCompleted);
        Assert.Equal(1, (await reply).Frame);
    }

    [Fact]
    public async Task CallRunsTheRegisteredVerbOnThePumpThread()
    {
        using AutomationHost host = NewHost();
        int verbThread = 0;
        JsonNode? seenArguments = null;
        host.Register("walk_to", args =>
        {
            verbThread = Environment.CurrentManagedThreadId;
            seenArguments = JsonNode.Parse(args.GetRawText());
            return new JsonObject { ["arrived"] = true };
        });

        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse(
            "{\"id\":4,\"cmd\":\"call\",\"name\":\"walk_to\",\"args\":{\"tile\":7}}"));
        Assert.Equal(0, verbThread);                            // Submit queues, it does not run the verb

        int pumpThread = 0;
        var pump = new Thread(() => { pumpThread = Environment.CurrentManagedThreadId; host.Pump(Frame); });
        pump.Start();
        pump.Join();

        Assert.Equal(pumpThread, verbThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, verbThread);
        Assert.Equal(7, seenArguments!["tile"]!.GetValue<int>());

        AutomationReply result = await reply;
        Assert.True(result.Ok!["arrived"]!.GetValue<bool>());
        Assert.Equal(1, result.Frame);
    }

    [Fact]
    public async Task AnUnknownVerbIsAnError()
    {
        using AutomationHost host = NewHost();
        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse(
            "{\"id\":1,\"cmd\":\"call\",\"name\":\"nope\"}"));

        host.Pump(Frame);

        Assert.Equal("unknown verb 'nope'", (await reply).Error);
    }

    [Fact]
    public async Task AThrowingVerbBecomesAnErrorReplyRatherThanKillingTheFrameLoop()
    {
        using AutomationHost host = NewHost();
        host.Register("boom", _ => throw new InvalidOperationException("the camera is not ready"));
        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse(
            "{\"id\":1,\"cmd\":\"call\",\"name\":\"boom\"}"));

        host.Pump(Frame);

        Assert.Equal("the camera is not ready", (await reply).Error);
    }

    [Fact]
    public async Task StateReturnsTheProvidersDocument()
    {
        using AutomationHost host = NewHost();
        host.StateProvider = () => new JsonObject { ["hp"] = 42, ["tile"] = "12,7" };

        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":2,\"cmd\":\"state\"}"));
        host.Pump(Frame);

        AutomationReply result = await reply;
        Assert.Equal(42, result.Ok!["hp"]!.GetValue<int>());
        Assert.Equal("12,7", result.Ok!["tile"]!.GetValue<string>());
        Assert.Equal(1, result.Frame);
    }

    [Fact]
    public async Task StateWithNoProviderIsAnError()
    {
        using AutomationHost host = NewHost();
        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":1,\"cmd\":\"state\"}"));

        host.Pump(Frame);

        Assert.Equal("no state provider is registered", (await reply).Error);
    }

    [Fact]
    public async Task QuitRunsTheWiredHandlerOnThePump()
    {
        using AutomationHost host = NewHost();
        bool quit = false;
        host.QuitRequested = () => quit = true;

        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":1,\"cmd\":\"quit\"}"));
        Assert.False(quit);

        host.Pump(Frame);

        Assert.True(quit);
        Assert.True((await reply).IsSuccess);
    }

    [Fact]
    public async Task PingAnswersWithoutAFrameSoTheBridgeCanCheckReadiness()
    {
        using AutomationHost host = NewHost();

        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":1,\"cmd\":\"ping\"}"));

        Assert.True(reply.IsCompleted);
        AutomationReply result = await reply;
        Assert.Equal(0, result.Frame);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AnUnknownCommandFailsImmediately()
    {
        using AutomationHost host = NewHost();

        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":1,\"cmd\":\"screenshot\"}"));

        Assert.True(reply.IsCompleted);
        Assert.Equal("unknown command 'screenshot'", (await reply).Error);
    }

    [Theory]
    [InlineData("{\"cmd\":\"input\",\"x\":10}", "'x' and 'y' must be given together")]
    [InlineData("{\"cmd\":\"input\",\"button\":\"scroll\"}", "unknown button 'scroll'")]
    [InlineData("{\"cmd\":\"input\",\"key\":\"Banana\"}", "unknown key 'Banana'")]
    [InlineData("{\"cmd\":\"input\",\"key\":\"None\"}", "'key' cannot be None")]
    [InlineData("{\"cmd\":\"input\",\"key\":\"W\",\"action\":\"wiggle\"}", "'action' must be 'press' or 'release'")]
    [InlineData("{\"cmd\":\"input\",\"key\":\"W\",\"holdFrames\":0}", "'holdFrames' must be at least 1")]
    [InlineData("{\"cmd\":\"input\",\"key\":\"W\",\"action\":\"release\",\"holdFrames\":2}", "'holdFrames' only applies to a press")]
    [InlineData("{\"cmd\":\"input\",\"x\":1,\"y\":2,\"holdFrames\":2}", "'holdFrames' needs a 'button' or a 'key' to hold")]
    [InlineData("{\"cmd\":\"input\"}", "'input' carries nothing to apply")]
    [InlineData("{\"cmd\":\"step\",\"frames\":0}", "'frames' must be at least 1")]
    [InlineData("{\"cmd\":\"step\",\"frames\":\"three\"}", "'frames' is not an integer")]
    [InlineData("{\"cmd\":\"call\"}", "'call' is missing a string 'name'")]
    public async Task ABadArgumentFailsAtSubmitTimeWithAPreciseMessage(string line, string expected)
    {
        using AutomationHost host = NewHost();

        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse(line));

        Assert.True(reply.IsCompleted);
        Assert.Equal(expected, (await reply).Error);
        Assert.Equal(0, host.Frame);                            // nothing queued, so nothing waits on a frame
    }

    [Fact]
    public void AReleaseCommandLiftsAnInjectedHold()
    {
        using AutomationHost host = NewHost();
        host.Submit(AutomationTestKit.Parse("{\"cmd\":\"input\",\"key\":\"A\"}"));
        Assert.True(host.Pump(Frame).IsDown(Key.A));

        host.Submit(AutomationTestKit.Parse("{\"cmd\":\"input\",\"key\":\"A\",\"action\":\"release\"}"));
        InputState composed = host.Pump(Frame);

        Assert.False(composed.IsDown(Key.A));
        Assert.True(composed.WasReleased(Key.A));
    }

    [Fact]
    public void ReleasePointerHandsTheCursorBack()
    {
        using AutomationHost host = NewHost();
        host.Submit(AutomationTestKit.Parse("{\"cmd\":\"input\",\"x\":50,\"y\":60}"));
        Assert.Equal(new Vector2(50, 60), host.Pump(Frame).MousePosition);

        host.Submit(AutomationTestKit.Parse("{\"cmd\":\"input\",\"releasePointer\":true}"));

        Assert.Equal(Frame.MousePosition, host.Pump(Frame).MousePosition);
    }

    [Fact]
    public async Task DisposeFailsACommandStillWaitingOnAFrame()
    {
        AutomationHost host = NewHost();
        Task<AutomationReply> reply = host.Submit(AutomationTestKit.Parse("{\"id\":1,\"cmd\":\"step\",\"frames\":100}"));
        host.Pump(Frame);
        Assert.False(reply.IsCompleted);

        host.Dispose();

        Assert.Equal("automation host stopped", (await reply).Error);
    }
}
