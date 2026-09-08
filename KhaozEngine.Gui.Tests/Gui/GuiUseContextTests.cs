using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public sealed class GuiUseContextTests
    {
        [Fact]
        public void Begin_starts_source_and_consumes_its_gesture()
        {
            var context = new GuiUseContext();
            Pointer pointer = FreshTap(new Vector2(20f, 20f));
            var source = new UsePayload("knife", "bag", 4);

            Assert.True(context.Begin(pointer, source));

            Assert.True(context.IsActive);
            Assert.Equal(source, context.Payload);
            Assert.True(pointer.IsConsumed);
            Assert.False(context.WasCompleted);
            Assert.False(context.WasCancelled);
        }

        [Fact]
        public void Begin_replaces_an_active_source_and_consumes_the_new_gesture()
        {
            var context = new GuiUseContext();
            Assert.True(context.Begin(FreshTap(new Vector2(20f, 20f)), new UsePayload("knife", "bag", 4)));
            Pointer replacementPointer = FreshTap(new Vector2(30f, 30f));
            var replacement = new UsePayload("chisel", "toolbelt", 2);

            Assert.True(context.Begin(replacementPointer, replacement));

            Assert.True(context.IsActive);
            Assert.Equal(replacement, context.Payload);
            Assert.True(replacementPointer.IsConsumed);
            Assert.False(context.WasCancelled);
        }

        [Fact]
        public void Accepted_target_returns_both_opaque_halves_and_clears_source()
        {
            var context = new GuiUseContext();
            Pointer pointer = FreshTap(new Vector2(20f, 20f));
            var source = new UsePayload("knife", "bag", 4);
            Assert.True(context.Begin(pointer, source));

            context.BeginFrame();
            pointer = FreshTap(new Vector2(40f, 40f));
            Assert.True(context.Complete(pointer, "bag", 7));
            Assert.Equal(new UseResult(source, new UseTarget("bag", 7)), context.LastUse);
            Assert.True(context.WasCompleted);
            Assert.False(context.IsActive);
            Assert.Equal(default, context.Payload);
            Assert.True(pointer.IsConsumed);
        }

        [Fact]
        public void Refusal_keeps_source_and_does_not_consume()
        {
            (GuiUseContext context, UsePayload source) = ActiveContext();
            Pointer pointer = FreshTap(new Vector2(40f, 40f));

            Assert.False(context.Complete(pointer, "world", 19, accepted: false));

            Assert.True(context.IsActive);
            Assert.Equal(source, context.Payload);
            Assert.False(context.WasCompleted);
            Assert.False(pointer.IsConsumed);
        }

        [Fact]
        public void Accepted_completion_while_idle_does_not_consume()
        {
            var context = new GuiUseContext();
            Pointer pointer = FreshTap(new Vector2(40f, 40f));

            Assert.False(context.Complete(pointer, "world", 19));

            Assert.False(context.WasCompleted);
            Assert.False(pointer.IsConsumed);
        }

        [Fact]
        public void CompleteIn_requires_a_tap_that_began_and_ended_in_bounds()
        {
            (GuiUseContext context, UsePayload source) = ActiveContext();
            var bounds = new Rect(30f, 30f, 40f, 40f);
            Pointer enteredDuringGesture = TapFrom(new Vector2(10f, 10f), new Vector2(40f, 40f));

            Assert.False(context.CompleteIn(enteredDuringGesture, bounds, "altar", 3));
            Assert.True(context.IsActive);
            Assert.Equal(source, context.Payload);
            Assert.False(enteredDuringGesture.IsConsumed);

            Pointer freshTapInside = FreshTap(new Vector2(40f, 40f));
            Assert.True(context.CompleteIn(freshTapInside, bounds, "altar", 3));
            Assert.Equal(new UseResult(source, new UseTarget("altar", 3)), context.LastUse);
            Assert.True(freshTapInside.IsConsumed);
        }

        [Fact]
        public void CompleteIn_refusal_does_not_consume_a_matching_tap()
        {
            (GuiUseContext context, UsePayload source) = ActiveContext();
            var bounds = new Rect(30f, 30f, 40f, 40f);
            Pointer pointer = FreshTap(new Vector2(40f, 40f));

            Assert.False(context.CompleteIn(pointer, bounds, "altar", accepted: false));

            Assert.True(context.IsActive);
            Assert.Equal(source, context.Payload);
            Assert.False(pointer.IsConsumed);
        }

        [Fact]
        public void Cancel_records_the_source_and_clears_it()
        {
            (GuiUseContext context, UsePayload source) = ActiveContext();

            context.Cancel();

            Assert.True(context.WasCancelled);
            Assert.Equal(source, context.CancelledPayload);
            Assert.False(context.IsActive);
            Assert.Equal(default, context.Payload);
            Assert.False(context.WasCompleted);
        }

        [Fact]
        public void Cancel_is_idempotent_while_idle()
        {
            (GuiUseContext context, UsePayload source) = ActiveContext();
            context.Cancel();

            context.Cancel();

            Assert.True(context.WasCancelled);
            Assert.Equal(source, context.CancelledPayload);

            context.BeginFrame();
            context.Cancel();
            Assert.False(context.WasCancelled);
        }

        [Fact]
        public void BeginFrame_clears_result_flags_without_clearing_an_active_source()
        {
            (GuiUseContext context, UsePayload source) = ActiveContext();
            context.Cancel();
            Assert.True(context.WasCancelled);

            context.BeginFrame();

            Assert.False(context.WasCancelled);
            Assert.False(context.WasCompleted);

            Assert.True(context.Begin(FreshTap(new Vector2(20f, 20f)), source));
            context.BeginFrame();
            Assert.True(context.IsActive);
            Assert.Equal(source, context.Payload);

            Assert.True(context.Complete(FreshTap(new Vector2(40f, 40f)), "world", 9));
            Assert.True(context.WasCompleted);
            context.BeginFrame();
            Assert.False(context.WasCompleted);
            Assert.False(context.WasCancelled);
        }

        [Fact]
        public void Opaque_values_are_preserved_by_reference()
        {
            var token = new object();
            var sourceId = new object();
            var targetId = new object();
            var source = new UsePayload(token, sourceId, 23);
            var context = new GuiUseContext();

            Assert.True(context.Begin(FreshTap(new Vector2(20f, 20f)), source));
            Assert.Same(token, context.Payload.Token);
            Assert.Same(sourceId, context.Payload.SourceId);
            Assert.Equal(23, context.Payload.SourceIndex);

            Assert.True(context.Complete(FreshTap(new Vector2(40f, 40f)), targetId, 31));
            Assert.Same(token, context.LastUse.Source.Token);
            Assert.Same(sourceId, context.LastUse.Source.SourceId);
            Assert.Equal(23, context.LastUse.Source.SourceIndex);
            Assert.Same(targetId, context.LastUse.Target.TargetId);
            Assert.Equal(31, context.LastUse.Target.TargetIndex);
        }

        static (GuiUseContext Context, UsePayload Source) ActiveContext()
        {
            var context = new GuiUseContext();
            var source = new UsePayload("knife", "bag", 4);
            Assert.True(context.Begin(FreshTap(new Vector2(20f, 20f)), source));
            context.BeginFrame();
            return (context, source);
        }

        static Pointer FreshTap(Vector2 position) => TapFrom(position, position);

        static Pointer TapFrom(Vector2 pressOrigin, Vector2 releasePosition)
        {
            var pointer = new Pointer();
            pointer.Update(Frame(pressOrigin, down: false));
            pointer.Update(Frame(pressOrigin, down: true));
            pointer.Update(Frame(releasePosition, down: false));
            Assert.True(pointer.IsJustReleased);
            return pointer;
        }

        static InputState Frame(Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, new HashSet<MouseButton>(),
                position, Vector2.Zero, 0f, 960, 540);
        }
    }
}
