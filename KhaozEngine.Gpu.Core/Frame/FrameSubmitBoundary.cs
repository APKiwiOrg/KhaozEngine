using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Frame;

/// <summary>Owns fixed-capacity native storage for one frame. Serialize operations on each instance.</summary>
public sealed class FrameSubmitBoundary : IDisposable
{
    private readonly FrameHandle _frame;

    public FrameSubmitBoundary(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _frame = new FrameHandle((uint)capacity);
    }

    /// <summary>Copies a frame into native storage in ascending key order, preserving the order of ties.</summary>
    public unsafe void Submit(ReadOnlySpan<khaoz_draw_record> records)
    {
        bool acquired = false;
        try
        {
            _frame.DangerousAddRef(ref acquired);
            fixed (khaoz_draw_record* data = records)
            {
                var frame = (khaoz_frame*)_frame.DangerousGetHandle();
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_frame_submit(frame, data, (uint)records.Length),
                    nameof(KhaozNative.khaoz_frame_submit));
            }
        }
        finally
        {
            if (acquired)
                _frame.DangerousRelease();
        }
    }

    /// <summary>Copies every sorted key into the destination and returns the number written.</summary>
    public unsafe int CopySortedKeys(Span<ulong> destination)
    {
        bool acquired = false;
        try
        {
            _frame.DangerousAddRef(ref acquired);
            fixed (ulong* keys = destination)
            {
                var frame = (khaoz_frame*)_frame.DangerousGetHandle();
                uint count;
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_frame_sorted_keys(frame, keys, (uint)destination.Length, &count),
                    nameof(KhaozNative.khaoz_frame_sorted_keys));
                return (int)count;
            }
        }
        finally
        {
            if (acquired)
                _frame.DangerousRelease();
        }
    }

    public void Dispose() => _frame.Dispose();

    private sealed unsafe class FrameHandle : SafeHandle
    {
        public FrameHandle(uint capacity) : base(IntPtr.Zero, ownsHandle: true)
        {
            khaoz_frame* frame;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_frame_create(capacity, &frame),
                nameof(KhaozNative.khaoz_frame_create));
            SetHandle((nint)frame);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            KhaozNative.khaoz_frame_destroy((khaoz_frame*)handle);
            return true;
        }
    }
}
