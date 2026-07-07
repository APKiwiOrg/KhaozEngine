using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Actions;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless tests for the action-map layer (<see cref="ActionMap"/>, <see cref="InputSource"/>,
    /// <see cref="RebindOperation"/>, <see cref="ActionMapSerializer"/>, <see cref="ActionMapController"/>): action
    /// evaluation for all three kinds from synthetic <see cref="InputState"/> sequences, edge detection across frames,
    /// WASD 2D normalization, multi-binding OR/sum-clamp semantics, per-player gamepad isolation, the rebind capture
    /// flow, and serialization round-trip + degradation. Everything is snapshot-driven; nothing touches Silk.NET.
    /// </summary>
    public class ActionMapTests
    {
        // ---- frame builders -------------------------------------------------

        static InputState Frame(
            IEnumerable<Key>? down = null, IEnumerable<Key>? pressed = null, IEnumerable<Key>? released = null,
            IEnumerable<MouseButton>? mouseDown = null, IEnumerable<MouseButton>? mousePressed = null,
            params GamepadState[] pads)
        {
            var d = new HashSet<Key>(down ?? Array.Empty<Key>());
            var p = new HashSet<Key>(pressed ?? Array.Empty<Key>());
            foreach (var k in p) d.Add(k); // a key pressed this frame is also held this frame
            var md = new HashSet<MouseButton>(mouseDown ?? Array.Empty<MouseButton>());
            var mp = new HashSet<MouseButton>(mousePressed ?? Array.Empty<MouseButton>());
            foreach (var b in mp) md.Add(b);
            return new InputState(
                d, p, new HashSet<Key>(released ?? Array.Empty<Key>()),
                md, mp, Vector2.Zero, Vector2.Zero, 0f, 960, 540, pads);
        }

        static GamepadState Pad(
            int index = 0, IEnumerable<GamepadButton>? down = null, IEnumerable<GamepadButton>? pressed = null,
            IEnumerable<GamepadButton>? released = null, Vector2 left = default, Vector2 right = default,
            float leftTrigger = 0f, float rightTrigger = 0f)
        {
            var d = new HashSet<GamepadButton>(down ?? Array.Empty<GamepadButton>());
            var p = new HashSet<GamepadButton>(pressed ?? Array.Empty<GamepadButton>());
            foreach (var b in p) d.Add(b);
            return new GamepadState(
                index, d, p, new HashSet<GamepadButton>(released ?? Array.Empty<GamepadButton>()),
                left, right, leftTrigger, rightTrigger);
        }

        // ---- button action evaluation + edge detection ----------------------

        [Fact]
        public void Button_IsDown_WasPressed_WasReleased_AcrossFrames()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));

            map.Update(Frame()); // frame 0: nothing
            Assert.False(map.IsDown("jump"));
            Assert.False(map.WasPressed("jump"));

            map.Update(Frame(pressed: new[] { Key.Space })); // frame 1: press edge
            Assert.True(map.IsDown("jump"));
            Assert.True(map.WasPressed("jump"));
            Assert.False(map.WasReleased("jump"));

            map.Update(Frame(down: new[] { Key.Space })); // frame 2: held, no new press
            Assert.True(map.IsDown("jump"));
            Assert.False(map.WasPressed("jump"));
            Assert.False(map.WasReleased("jump"));

            map.Update(Frame()); // frame 3: released
            Assert.False(map.IsDown("jump"));
            Assert.False(map.WasPressed("jump"));
            Assert.True(map.WasReleased("jump"));
        }

        [Fact]
        public void Button_MultipleBindings_OrTogether()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("fire"),
                InputSource.FromKey(Key.LeftControl),
                InputSource.FromMouseButton(MouseButton.Left),
                InputSource.FromGamepadButton(GamepadButton.RightShoulder));

            map.Update(Frame(mouseDown: new[] { MouseButton.Left }));
            Assert.True(map.IsDown("fire")); // any binding down -> down

            map.Update(Frame(pads: Pad(down: new[] { GamepadButton.RightShoulder })));
            Assert.True(map.IsDown("fire")); // different binding down -> still down

            map.Update(Frame());
            Assert.False(map.IsDown("fire"));
        }

        [Fact]
        public void Button_PressEdge_DoesNotDoubleFire_WhenTwoBindingsOverlap()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("fire"),
                InputSource.FromKey(Key.LeftControl), InputSource.FromKey(Key.RightControl));

            map.Update(Frame());
            map.Update(Frame(pressed: new[] { Key.LeftControl })); // first binding presses
            Assert.True(map.WasPressed("fire"));
            map.Update(Frame(down: new[] { Key.LeftControl }, pressed: new[] { Key.RightControl })); // second presses while first held
            Assert.False(map.WasPressed("fire")); // already down last frame -> no new press edge
            Assert.True(map.IsDown("fire"));
        }

        // ---- axis1D evaluation ---------------------------------------------

        [Fact]
        public void Axis1D_CompositeTwoKeys()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis1D("turn"), InputSource.Axis1D(negative: Key.A, positive: Key.D));

            map.Update(Frame(down: new[] { Key.D }));
            Assert.Equal(1f, map.GetAxis("turn"), 3);

            map.Update(Frame(down: new[] { Key.A }));
            Assert.Equal(-1f, map.GetAxis("turn"), 3);

            map.Update(Frame(down: new[] { Key.A, Key.D }));
            Assert.Equal(0f, map.GetAxis("turn"), 3); // both cancel
        }

        [Fact]
        public void Axis1D_Trigger_ReadsAnalog()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis1D("gas"), InputSource.Trigger(GamepadTriggerSide.Right));
            map.Update(Frame(pads: Pad(rightTrigger: 0.4f)));
            Assert.Equal(0.4f, map.GetAxis("gas"), 3);
        }

        [Fact]
        public void Axis1D_MultipleBindings_SumThenClamp()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis1D("throttle"),
                InputSource.Trigger(GamepadTriggerSide.Right),
                InputSource.FromKey(Key.W)); // key contributes +1

            map.Update(Frame(down: new[] { Key.W }, pads: Pad(rightTrigger: 0.3f)));
            Assert.Equal(1f, map.GetAxis("throttle"), 3); // 0.3 + 1 = 1.3 clamped to 1
        }

        // ---- axis2D + WASD normalization -----------------------------------

        [Fact]
        public void Axis2D_Wasd_CardinalIsUnit()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis2D("move"), InputSource.WasdDefault);

            map.Update(Frame(down: new[] { Key.D })); // right
            Assert.Equal(new Vector2(1f, 0f), map.GetAxis2D("move"));

            map.Update(Frame(down: new[] { Key.W })); // up (+Y)
            Assert.Equal(new Vector2(0f, 1f), map.GetAxis2D("move"));
        }

        [Fact]
        public void Axis2D_Wasd_DiagonalIsNormalizedToUnitLength()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis2D("move"), InputSource.WasdDefault);

            map.Update(Frame(down: new[] { Key.D, Key.W })); // up-right diagonal
            var v = map.GetAxis2D("move");
            Assert.Equal(1f, v.Length(), 3); // not 1.414 -> no diagonal speed boost
            Assert.Equal(v.X, v.Y, 3);
            Assert.True(v.X > 0 && v.Y > 0);
        }

        [Fact]
        public void Axis2D_Stick_KeepsAnalogMagnitude()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis2D("move"), InputSource.StickAxis(GamepadStick.Left, StickComponent.X));
            // half-tilt right stick; a partial deflection must NOT be normalized up to 1.
            map.Update(Frame(pads: Pad(left: new Vector2(0.5f, 0f))));
            var v = map.GetAxis2D("move");
            Assert.True(v.X > 0.2f && v.X < 0.6f, $"expected partial magnitude, got {v.X}");
            Assert.True(v.Length() <= 1.0001f);
        }

        [Fact]
        public void Axis2D_FullStick_IsClampedToUnitLength()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis2D("move"), InputSource.StickAxis(GamepadStick.Left, StickComponent.X));
            map.Update(Frame(pads: Pad(left: new Vector2(1f, 1f)))); // corner (over-unit raw)
            Assert.True(map.GetAxis2D("move").Length() <= 1.0001f);
        }

        [Fact]
        public void Axis2D_StickInvertY_FlipsSign()
        {
            var map = new ActionMap();
            // A single stick-axis source reads the whole stick; invert flips both components in this reading.
            map.AddAction(InputAction.Axis2D("look"),
                InputSource.StickAxis(GamepadStick.Right, StickComponent.Y, invert: true));
            map.Update(Frame(pads: Pad(right: new Vector2(0f, 0.8f))));
            Assert.True(map.GetAxis2D("look").Y < 0f);
        }

        // ---- per-player gamepad isolation ----------------------------------

        [Fact]
        public void PerPlayer_GamepadIsolation()
        {
            var p1 = new ActionMap(PlayerIndex.One);
            var p2 = new ActionMap(PlayerIndex.Two);
            p1.AddAction(InputAction.Button("jump"), InputSource.FromGamepadButton(GamepadButton.A));
            p2.AddAction(InputAction.Button("jump"), InputSource.FromGamepadButton(GamepadButton.A));

            // Player two's pad presses A; player one's pad is idle.
            var frame = Frame(pads: new[] { Pad(index: 0), Pad(index: 1, down: new[] { GamepadButton.A }) });
            p1.Update(frame);
            p2.Update(frame);

            Assert.False(p1.IsDown("jump")); // pad 0 idle
            Assert.True(p2.IsDown("jump"));  // pad 1 pressed
        }

        [Fact]
        public void PerPlayer_StickIsolation()
        {
            var p2 = new ActionMap(PlayerIndex.Two);
            p2.AddAction(InputAction.Axis2D("move"), InputSource.StickAxis(GamepadStick.Left, StickComponent.X));
            var frame = Frame(pads: new[]
            {
                Pad(index: 0, left: new Vector2(1f, 0f)), // pad 0 fully right
                Pad(index: 1, left: new Vector2(0f, 0f)), // pad 1 idle
            });
            p2.Update(frame);
            Assert.Equal(0f, p2.GetAxis2D("move").X, 3); // reads pad 1, which is idle
        }

        // ---- rebind capture flow -------------------------------------------

        [Fact]
        public void Rebind_CapturesKey_AndReplacesBinding()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            var op = new RebindOperation(map, "jump", slot: 0);

            Assert.Equal(RebindStatus.Listening, op.Feed(Frame())); // nothing pressed
            Assert.Equal(RebindStatus.Captured, op.Feed(Frame(pressed: new[] { Key.J })));
            Assert.Equal(InputSourceKind.Key, op.Captured.Kind);
            Assert.Equal(Key.J, op.Captured.Key);

            // The binding is now J, not Space.
            map.Update(Frame(pressed: new[] { Key.J }));
            Assert.True(map.IsDown("jump"));
            map.Update(Frame(pressed: new[] { Key.Space }));
            Assert.False(map.IsDown("jump"));
        }

        [Fact]
        public void Rebind_CapturesGamepadButton()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            var op = new RebindOperation(map, "jump", slot: 0);
            var status = op.Feed(Frame(pads: Pad(pressed: new[] { GamepadButton.X })));
            Assert.Equal(RebindStatus.Captured, status);
            Assert.Equal(InputSourceKind.GamepadButton, op.Captured.Kind);
            Assert.Equal(GamepadButton.X, op.Captured.GamepadButton);
        }

        [Fact]
        public void Rebind_CapturesFullTiltStickAsAxis()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Axis2D("move"), InputSource.WasdDefault);
            var op = new RebindOperation(map, "move", slot: 0);
            Assert.Equal(RebindStatus.Listening, op.Feed(Frame(pads: Pad(left: new Vector2(0.3f, 0f))))); // resting, below threshold
            Assert.Equal(RebindStatus.Captured, op.Feed(Frame(pads: Pad(left: new Vector2(0.9f, 0f))))); // full tilt
            Assert.Equal(InputSourceKind.GamepadStickAxis, op.Captured.Kind);
        }

        [Fact]
        public void Rebind_ExcludesCancelKeyByDefault()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            var op = new RebindOperation(map, "jump", slot: 0);
            var status = op.Feed(Frame(pressed: new[] { Key.Escape })); // Escape is the default cancel
            Assert.Equal(RebindStatus.Cancelled, status);
            // Binding unchanged: still Space.
            map.Update(Frame(pressed: new[] { Key.Space }));
            Assert.True(map.IsDown("jump"));
        }

        [Fact]
        public void Rebind_CustomExclusion_DoesNotCapture()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            var op = new RebindOperation(map, "jump", slot: 0, excludeKeys: new[] { Key.Escape, Key.Tab });
            Assert.Equal(RebindStatus.Cancelled, op.Feed(Frame(pressed: new[] { Key.Tab })));
        }

        [Fact]
        public void Rebind_IgnoresHeldKey_UntilRePressed()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            var op = new RebindOperation(map, "jump", slot: 0);
            // Key held (down) but no press edge -> not captured.
            Assert.Equal(RebindStatus.Listening, op.Feed(Frame(down: new[] { Key.K })));
            // Press edge -> captured.
            Assert.Equal(RebindStatus.Captured, op.Feed(Frame(pressed: new[] { Key.K })));
        }

        [Fact]
        public void Rebind_TerminalFeedsAreNoOps()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            var op = new RebindOperation(map, "jump", slot: 0);
            op.Feed(Frame(pressed: new[] { Key.J }));
            Assert.Equal(RebindStatus.Captured, op.Status);
            Assert.Equal(RebindStatus.Captured, op.Feed(Frame(pressed: new[] { Key.K }))); // ignored
            Assert.Equal(Key.J, op.Captured.Key);
        }

        [Fact]
        public void Rebind_AppendSlot_AddsBinding()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            Assert.Equal(1, map.BindingCount("jump"));
            var op = new RebindOperation(map, "jump", slot: 1); // one past end -> append
            op.Feed(Frame(pressed: new[] { Key.J }));
            Assert.Equal(2, map.BindingCount("jump"));
        }

        // ---- serialization --------------------------------------------------

        [Fact]
        public void Serialize_RoundTrips_AllSourceKinds()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space), InputSource.FromGamepadButton(GamepadButton.A));
            map.AddAction(InputAction.Button("click"), InputSource.FromMouseButton(MouseButton.Right));
            map.AddAction(InputAction.Axis1D("turn"), InputSource.Axis1D(Key.A, Key.D));
            map.AddAction(InputAction.Axis2D("move"), InputSource.WasdDefault, InputSource.StickAxis(GamepadStick.Left, StickComponent.X));
            map.AddAction(InputAction.Axis1D("gas"), InputSource.Trigger(GamepadTriggerSide.Right, scale: 1f));

            string json = ActionMapSerializer.Serialize(map);

            // Rebuild a fresh map with DIFFERENT (default) bindings, then apply the persisted overrides.
            var reloaded = new ActionMap();
            reloaded.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Enter));
            reloaded.AddAction(InputAction.Button("click"), InputSource.FromMouseButton(MouseButton.Left));
            reloaded.AddAction(InputAction.Axis1D("turn"), InputSource.Axis1D(Key.Left, Key.Right));
            reloaded.AddAction(InputAction.Axis2D("move"), InputSource.ArrowsDefault);
            reloaded.AddAction(InputAction.Axis1D("gas"), InputSource.Trigger(GamepadTriggerSide.Left));
            int applied = ActionMapSerializer.Load(reloaded, json);
            Assert.Equal(5, applied);

            // The reloaded bindings now match the originals.
            for (int i = 0; i < map.ActionIds.Count; i++)
            {
                string id = map.ActionIds[i];
                var a = map.BindingsOf(id);
                var b = reloaded.BindingsOf(id);
                Assert.Equal(a.Count, b.Count);
                for (int s = 0; s < a.Count; s++) Assert.Equal(a[s].Source, b[s].Source);
            }
        }

        [Fact]
        public void Serialize_HasVersionField()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            string json = ActionMapSerializer.Serialize(map);
            Assert.Contains("\"version\"", json, StringComparison.OrdinalIgnoreCase);
            var doc = ActionMapSerializer.Deserialize(json);
            Assert.Equal(ActionMapSerializer.CurrentVersion, doc.Version);
        }

        [Fact]
        public void Deserialize_UnknownSourceKind_DegradesToDefaults()
        {
            // A persisted entry whose only binding has an unknown kind must leave the action on its code default,
            // not unbound.
            string json = """
            {
              "version": 1,
              "actions": [
                { "action": "jump", "bindings": [ { "kind": "SomeFutureKind99", "key": "Space" } ] }
              ]
            }
            """;
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Enter)); // code default = Enter
            int applied = ActionMapSerializer.Load(map, json);
            Assert.Equal(0, applied); // the entry had no valid bindings -> skipped
            map.Update(Frame(pressed: new[] { Key.Enter }));
            Assert.True(map.IsDown("jump")); // default Enter still bound
        }

        [Fact]
        public void Deserialize_MalformedJson_YieldsEmptyDocument_DefaultsStand()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Enter));
            int applied = ActionMapSerializer.Load(map, "{ this is not valid json ");
            Assert.Equal(0, applied);
            map.Update(Frame(pressed: new[] { Key.Enter }));
            Assert.True(map.IsDown("jump"));
        }

        [Fact]
        public void Deserialize_UnknownAction_IsIgnored()
        {
            string json = """
            { "version": 1, "actions": [ { "action": "not.declared", "bindings": [ { "kind": "Key", "key": "J" } ] } ] }
            """;
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            int applied = ActionMapSerializer.Load(map, json);
            Assert.Equal(0, applied);
        }

        [Fact]
        public void Serialize_PartialOverride_KeepsUntouchedActionDefaults()
        {
            var map = new ActionMap();
            map.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.J)); // will be persisted
            string json = ActionMapSerializer.Serialize(map);

            var reloaded = new ActionMap();
            reloaded.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space)); // default
            reloaded.AddAction(InputAction.Button("crouch"), InputSource.FromKey(Key.C)); // NOT in the persisted file
            ActionMapSerializer.Load(reloaded, json);

            reloaded.Update(Frame(pressed: new[] { Key.J, Key.C }));
            Assert.True(reloaded.IsDown("jump"));   // overridden to J
            Assert.True(reloaded.IsDown("crouch")); // kept default C
        }

        // ---- turn-key controller -------------------------------------------

        [Fact]
        public void Controller_LoadsPersisted_EvaluatesAndAutosavesOnRebind()
        {
            // First run: build defaults, persist.
            var declare = new Func<ActionMap>(() =>
            {
                var m = new ActionMap();
                m.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
                return m;
            });

            string? saved = null;
            var controller = new ActionMapController(declare(), persistedJson: null, save: s => saved = s);

            // Evaluate: default Space works.
            controller.Update(Frame(pressed: new[] { Key.Space }));
            Assert.True(controller.IsDown("jump"));

            // Rebind jump to J; controller auto-saves through the sink on capture.
            controller.BeginRebind("jump", slot: 0);
            Assert.True(controller.IsRebinding);
            controller.Update(Frame(pressed: new[] { Key.J }));
            Assert.False(controller.IsRebinding);
            Assert.NotNull(saved);

            // A brand-new controller loading the saved string starts with J bound.
            var reloaded = new ActionMapController(declare(), persistedJson: saved);
            reloaded.Update(Frame(pressed: new[] { Key.J }));
            Assert.True(reloaded.IsDown("jump"));
            reloaded.Update(Frame(pressed: new[] { Key.Space }));
            Assert.False(reloaded.IsDown("jump"));
        }

        [Fact]
        public void Controller_RebindCancel_LeavesBindingAndClearsFlag()
        {
            var m = new ActionMap();
            m.AddAction(InputAction.Button("jump"), InputSource.FromKey(Key.Space));
            var controller = new ActionMapController(m);
            controller.BeginRebind("jump", slot: 0);
            controller.Update(Frame(pressed: new[] { Key.Escape })); // cancel
            Assert.False(controller.IsRebinding);
            controller.Update(Frame(pressed: new[] { Key.Space }));
            Assert.True(controller.IsDown("jump")); // unchanged
        }

        // ---- guardrails -----------------------------------------------------

        [Fact]
        public void ReadingUndeclaredAction_Throws()
        {
            var map = new ActionMap();
            Assert.Throws<KeyNotFoundException>(() => map.IsDown("nope"));
        }

        [Fact]
        public void InputAction_EmptyId_Throws()
        {
            Assert.Throws<ArgumentException>(() => new InputAction("", InputActionKind.Button));
        }
    }
}
