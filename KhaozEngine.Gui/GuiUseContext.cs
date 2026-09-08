using System;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    public readonly record struct UsePayload(object? Token, object? SourceId = null, int SourceIndex = -1);

    public readonly record struct UseTarget(object? TargetId, int TargetIndex = -1);

    public readonly record struct UseResult(UsePayload Source, UseTarget Target);

    public sealed class GuiUseContext
    {
        public bool IsActive { get; private set; }

        public UsePayload Payload { get; private set; }

        public bool WasCompleted { get; private set; }

        public UseResult LastUse { get; private set; }

        public bool WasCancelled { get; private set; }

        public UsePayload CancelledPayload { get; private set; }

        public void BeginFrame()
        {
            WasCompleted = false;
            WasCancelled = false;
        }

        public bool Begin(Pointer pointer, in UsePayload payload)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            Payload = payload;
            IsActive = true;
            pointer.ConsumeGesture();
            return true;
        }

        public bool Complete(Pointer pointer, object? targetId, int targetIndex = -1, bool accepted = true)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            if (!IsActive || !accepted) return false;

            LastUse = new UseResult(Payload, new UseTarget(targetId, targetIndex));
            WasCompleted = true;
            IsActive = false;
            Payload = default;
            pointer.ConsumeGesture();
            return true;
        }

        public bool CompleteIn(
            Pointer pointer,
            Rect bounds,
            object? targetId,
            int targetIndex = -1,
            bool accepted = true)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            if (!IsActive || !accepted || !pointer.IsTapIn(bounds)) return false;
            return Complete(pointer, targetId, targetIndex);
        }

        public void Cancel()
        {
            if (!IsActive) return;

            CancelledPayload = Payload;
            WasCancelled = true;
            IsActive = false;
            Payload = default;
        }
    }
}
