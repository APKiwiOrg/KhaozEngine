using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public sealed class GuiUseContextNullTests
    {
        [Fact]
        public void Begin_rejects_a_null_pointer_without_changing_public_state()
        {
            GuiUseContext context = PopulatedContext();
            ContextState before = Snapshot(context);

            Assert.Throws<ArgumentNullException>(
                () => context.Begin(null!, new UsePayload(new object(), new object(), 8)));

            Assert.Equal(before, Snapshot(context));
        }

        [Fact]
        public void Complete_rejects_a_null_pointer_without_changing_public_state()
        {
            GuiUseContext context = PopulatedContext();
            ContextState before = Snapshot(context);

            Assert.Throws<ArgumentNullException>(() => context.Complete(null!, "target", 9));

            Assert.Equal(before, Snapshot(context));
        }

        [Fact]
        public void CompleteIn_rejects_a_null_pointer_without_changing_public_state()
        {
            GuiUseContext context = PopulatedContext();
            ContextState before = Snapshot(context);

            Assert.Throws<ArgumentNullException>(
                () => context.CompleteIn(null!, new Rect(0f, 0f, 20f, 20f), "target", 9));

            Assert.Equal(before, Snapshot(context));
        }

        static GuiUseContext PopulatedContext()
        {
            var context = new GuiUseContext();
            context.Begin(FreshTap(), new UsePayload(new object(), new object(), 1));
            context.Complete(FreshTap(), new object(), 2);
            context.Begin(FreshTap(), new UsePayload(new object(), new object(), 3));
            context.Cancel();
            context.Begin(FreshTap(), new UsePayload(new object(), new object(), 4));
            return context;
        }

        static ContextState Snapshot(GuiUseContext context) => new(
            context.IsActive,
            context.Payload,
            context.WasCompleted,
            context.LastUse,
            context.WasCancelled,
            context.CancelledPayload);

        static Pointer FreshTap()
        {
            var pointer = new Pointer();
            Vector2 position = new(10f, 10f);
            pointer.Update(Frame(position, down: false));
            pointer.Update(Frame(position, down: true));
            pointer.Update(Frame(position, down: false));
            return pointer;
        }

        static InputState Frame(Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, new HashSet<MouseButton>(), position, Vector2.Zero, 0f, 960, 540);
        }

        readonly record struct ContextState(
            bool IsActive,
            UsePayload Payload,
            bool WasCompleted,
            UseResult LastUse,
            bool WasCancelled,
            UsePayload CancelledPayload);
    }
}
