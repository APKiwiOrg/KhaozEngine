using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.Windowing;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// The command half of <see cref="AutomationHost"/>: argument validation on the caller's thread, and application
    /// on the window thread. Split from the lifecycle file because they are separate concerns and the validation is
    /// the part that grows with the command table.
    /// <para>
    /// Validation runs at SUBMIT time so a bad argument fails immediately with a precise message instead of a frame
    /// later, and so the window thread does no JSON parsing. A verb or a state provider that throws becomes an error
    /// reply rather than an exception escaping the frame loop.
    /// </para>
    /// </summary>
    public sealed partial class AutomationHost
    {
        /// <summary>One queued command: the request, its parsed arguments, and the reply nobody has produced yet.</summary>
        sealed class PendingCommand
        {
            public required AutomationRequest Request { get; init; }
            public TaskCompletionSource<AutomationReply> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public InjectedInput Input { get; init; }
            public int Frames { get; init; }
            public string VerbName { get; init; } = "";
            public JsonElement VerbArguments { get; init; }
            /// <summary>The frame a <c>step</c> replies on. Set when the command is applied, not when it is queued.</summary>
            public long DueFrame { get; set; }
        }

        /// <summary>One <c>input</c> command's parsed intent, so the window thread applies values rather than JSON.</summary>
        readonly record struct InjectedInput(
            Vector2? PointerPosition, bool ReleasePointer, MouseButton? Button, Key? KeyCode, bool Press, int HoldFrames);

        /// <summary>Validate a request and build its queue entry. False means the caller gets an error reply now.</summary>
        bool TryPrepare(AutomationRequest request, out PendingCommand? pending, out string? error)
        {
            pending = null;
            error = null;
            switch (request.Command)
            {
                case "input":
                    if (!TryParseInput(request, out InjectedInput injected, out error)) return false;
                    pending = new PendingCommand { Request = request, Input = injected };
                    return true;

                case "step":
                    if (!request.TryReadInt("frames", out int frames, out error)) return false;
                    if (!IsPresent(request.Argument("frames"))) frames = 1;
                    if (frames < 1) { error = "'frames' must be at least 1"; return false; }
                    pending = new PendingCommand { Request = request, Frames = frames };
                    return true;

                case "state":
                case "quit":
                    pending = new PendingCommand { Request = request };
                    return true;

                case "call":
                    string? name = request.ReadString("name");
                    if (string.IsNullOrEmpty(name)) { error = "'call' is missing a string 'name'"; return false; }
                    pending = new PendingCommand
                    {
                        Request = request,
                        VerbName = name,
                        VerbArguments = request.Argument("args"),
                    };
                    return true;

                default:
                    error = "unknown command '" + request.Command + "'";
                    return false;
            }
        }

        /// <summary>
        /// Parse an <c>input</c> command. Pointer position is <c>x</c> plus <c>y</c> in window pixels and both are
        /// required together, <c>releasePointer</c> hands the cursor back to the real mouse, <c>button</c> and
        /// <c>key</c> name a <see cref="MouseButton"/> or a <see cref="Key"/> case-insensitively, <c>action</c> is
        /// <c>press</c> (the default) or <c>release</c>, and <c>holdFrames</c> schedules the auto-release.
        /// </summary>
        static bool TryParseInput(AutomationRequest request, out InjectedInput injected, out string? error)
        {
            injected = default;
            bool hasX = IsPresent(request.Argument("x"));
            bool hasY = IsPresent(request.Argument("y"));
            if (hasX != hasY) { error = "'x' and 'y' must be given together"; return false; }
            if (!request.TryReadFloat("x", out float x, out error)) return false;
            if (!request.TryReadFloat("y", out float y, out error)) return false;
            Vector2? pointer = hasX ? new Vector2(x, y) : null;

            if (!TryParseName(request, "button", out MouseButton? button, out error)) return false;
            if (!TryParseName(request, "key", out Key? key, out error)) return false;
            if (key == Key.None) { error = "'key' cannot be None"; return false; }

            string action = request.ReadString("action") ?? "press";
            bool press;
            if (string.Equals(action, "press", StringComparison.OrdinalIgnoreCase)) press = true;
            else if (string.Equals(action, "release", StringComparison.OrdinalIgnoreCase)) press = false;
            else { error = "'action' must be 'press' or 'release'"; return false; }

            if (!request.TryReadInt("holdFrames", out int hold, out error)) return false;
            bool hasHold = IsPresent(request.Argument("holdFrames"));
            if (hasHold && hold < 1) { error = "'holdFrames' must be at least 1"; return false; }
            if (hasHold && !press) { error = "'holdFrames' only applies to a press"; return false; }
            if (hasHold && button is null && key is null)
            {
                error = "'holdFrames' needs a 'button' or a 'key' to hold";
                return false;
            }

            bool releasePointer = request.ReadFlag("releasePointer");
            if (pointer is null && !releasePointer && button is null && key is null)
            {
                error = "'input' carries nothing to apply";
                return false;
            }

            injected = new InjectedInput(pointer, releasePointer, button, key, press, hasHold ? hold : 0);
            return true;
        }

        /// <summary>Parse an optional enum-named argument (a button or a key) case-insensitively.</summary>
        static bool TryParseName<TEnum>(AutomationRequest request, string argument, out TEnum? value, out string? error)
            where TEnum : struct, Enum
        {
            value = null;
            error = null;
            JsonElement element = request.Argument(argument);
            if (!IsPresent(element)) return true;
            if (element.ValueKind != JsonValueKind.String)
            {
                error = "'" + argument + "' is not a string";
                return false;
            }
            string name = element.GetString() ?? "";
            if (!Enum.TryParse(name, ignoreCase: true, out TEnum parsed) || !Enum.IsDefined(parsed))
            {
                error = "unknown " + argument + " '" + name + "'";
                return false;
            }
            value = parsed;
            return true;
        }

        /// <summary>True when the argument was supplied and is not JSON null.</summary>
        static bool IsPresent(JsonElement element) =>
            element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null;

        /// <summary>Apply one queued command on the window thread, at the frame boundary.</summary>
        void Apply(PendingCommand pending, long frame)
        {
            AutomationRequest request = pending.Request;
            switch (request.Command)
            {
                case "input":
                    ApplyInput(pending.Input, frame);
                    pending.Completion.TrySetResult(AutomationReply.Success(request.Id, frame, new JsonObject()));
                    break;

                case "step":
                    // Counted inclusive of this frame, so "step 1" replies on the frame it landed on.
                    pending.DueFrame = frame + pending.Frames - 1;
                    lock (_waitingLock) _waiting.Add(pending);
                    break;

                case "state":
                    Func<JsonNode?>? provider = StateProvider;
                    if (provider is null) Fail(pending, frame, "no state provider is registered");
                    else Complete(pending, frame, provider);
                    break;

                case "call":
                    if (!_verbs.TryGetValue(pending.VerbName, out Func<JsonElement, JsonNode?>? verb))
                        Fail(pending, frame, "unknown verb '" + pending.VerbName + "'");
                    else
                    {
                        JsonElement arguments = pending.VerbArguments;
                        Complete(pending, frame, () => verb(arguments));
                    }
                    break;

                case "quit":
                    Action? quit = QuitRequested;
                    if (quit is null) Fail(pending, frame, "no quit handler is wired");
                    else Complete(pending, frame, () => { quit(); return new JsonObject(); });
                    break;
            }
        }

        /// <summary>Push one parsed <c>input</c> into the injector.</summary>
        void ApplyInput(in InjectedInput input, long frame)
        {
            if (input.ReleasePointer) _injector.ReleasePointer();
            if (input.PointerPosition is Vector2 position) _injector.SetPointer(position);

            if (input.Button is MouseButton button)
            {
                if (input.Press) _injector.PressButton(button, frame, input.HoldFrames);
                else _injector.ReleaseButton(button);
            }
            if (input.KeyCode is Key key)
            {
                if (input.Press) _injector.PressKey(key, frame, input.HoldFrames);
                else _injector.ReleaseKey(key);
            }
        }

        /// <summary>Reply to every <c>step</c> whose frame has arrived, newest-last order preserved. Under the same
        /// lock <see cref="Dispose"/> takes to drain the list, so a dispose racing a frame cannot complete one
        /// command twice or drop another.</summary>
        void CompleteDue(long frame)
        {
            lock (_waitingLock)
            {
                for (int i = _waiting.Count - 1; i >= 0; i--)
                {
                    PendingCommand pending = _waiting[i];
                    if (pending.DueFrame > frame) continue;
                    pending.Completion.TrySetResult(AutomationReply.Success(pending.Request.Id, frame, new JsonObject()));
                    _waiting.RemoveAt(i);
                }
            }
        }

        /// <summary>Run a game callback and reply with what it produced, turning a throw into an error reply so one
        /// bad verb cannot take the frame loop down with it.</summary>
        static void Complete(PendingCommand pending, long frame, Func<JsonNode?> produce)
        {
            try
            {
                pending.Completion.TrySetResult(AutomationReply.Success(pending.Request.Id, frame, produce()));
            }
            catch (Exception ex)
            {
                pending.Completion.TrySetResult(AutomationReply.Failure(pending.Request.Id, frame, ex.Message));
            }
        }

        /// <summary>Reply with a failure on the frame the command was applied.</summary>
        static void Fail(PendingCommand pending, long frame, string error) =>
            pending.Completion.TrySetResult(AutomationReply.Failure(pending.Request.Id, frame, error));
    }
}
