using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>Archive-only identity probe. Stack walks and stderr writes invalidate FPS comparisons.</summary>
    [SupportedOSPlatform("macos")]
    internal sealed class MetalFrameLabelProbe
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("KE_METAL_FRAME_LABEL_PROBE") == "1";

        static long _nextBuffer;
        static long _nextList;
        static long _nextEncoder;
        readonly long _list = Interlocked.Increment(ref _nextList);
        long _recording;

        // Called only after a real device's ring acquisition, never for a fake command-list owner.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void BeginRecording(IntPtr buffer, MetalGpuDevice device)
        {
            long ordinal = ++_recording;
            long timestamp = Stopwatch.GetTimestamp();
            GpuDeviceCounters counters = device.Counters;
            string label = FormattableString.Invariant($"{BufferLabel(buffer)} list={_list} recording={ordinal}");
            SetLabel(buffer, label);
            Console.Error.WriteLine(FormattableString.Invariant(
                $"KE_METAL_FRAME_LABEL_PROBE Begin {label} stopwatchTicks={timestamp} stopwatchFrequency={Stopwatch.Frequency} backpressureStallCount={counters.BackpressureStallCount} backpressureStallMs={counters.BackpressureStallMs:R}"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void LabelNewCommandBuffer(IntPtr buffer)
        {
            if (!Enabled || buffer == IntPtr.Zero) return;
            long ordinal = Interlocked.Increment(ref _nextBuffer);
            SetLabel(buffer, FormattableString.Invariant($"KE Metal buffer={ordinal}"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void LabelEncoder(IntPtr buffer, IntPtr encoder, string kind)
        {
            if (!Enabled || encoder == IntPtr.Zero) return;
            long ordinal = Interlocked.Increment(ref _nextEncoder);
            string source = SourceFrames();
            SetLabel(encoder, FormattableString.Invariant(
                $"{BufferLabel(buffer)} encoder={ordinal} {kind} source={source}"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string SourceFrames()
        {
            var trace = new StackTrace(fNeedFileInfo: false);
            var source = new StringBuilder();
            var fallback = new StringBuilder();
            int sourceCount = 0;
            int fallbackCount = 0;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                MethodBase? method = trace.GetFrame(i)?.GetMethod();
                Type? type = method?.DeclaringType;
                if (type == null || method == null) continue;
                string ns = type.Namespace ?? "";
                if (InNamespace(ns, "KhaozEngine.Render3D")
                    || InNamespace(ns, "KhaozEngine.Terrain")
                    || InNamespace(ns, "KhaozEngine.TileWorld"))
                {
                    if (sourceCount == 8) continue;
                    AppendFrame(source, i, type, method);
                    sourceCount++;
                }
                else if (fallbackCount < 3
                    && !InNamespace(ns, "KhaozEngine.Gpu.Metal")
                    && !InNamespace(ns, "System")
                    && !InNamespace(ns, "Microsoft"))
                {
                    AppendFrame(fallback, i, type, method);
                    fallbackCount++;
                }
            }
            return sourceCount != 0 ? source.ToString()
                : "source-frames-unavailable callers=" + (fallbackCount != 0 ? fallback.ToString() : "unavailable");
        }

        static bool InNamespace(string ns, string prefix)
            => ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal);

        static void AppendFrame(StringBuilder text, int index, Type type, MethodBase method)
        {
            if (text.Length != 0) text.Append(" <- ");
            text.Append('[').Append(index).Append("] ").Append(type.FullName).Append('.').Append(method.Name);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string BufferLabel(IntPtr buffer)
            => new NSString(ObjCMsgSend.Send(buffer, Selectors.Label)).ToManaged();

        // The existing entry-point pool encloses both string creation and setLabel:. Metal copies the label.
        // NSString.FromManaged is autoreleased, so it must not be released here or kept past that pool.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetLabel(IntPtr target, string text)
        {
            NSString label = NSString.FromManaged(text);
            ObjCMsgSend.SendVoidPtr(target, Selectors.SetLabel, label.Handle);
        }

        // Keep native initialization behind macOS-only bodies, like the other ObjC selector caches.
        static class Selectors
        {
            internal static readonly IntPtr Label = ObjCRuntime.Sel("label");
            internal static readonly IntPtr SetLabel = ObjCRuntime.Sel("setLabel:");
        }
    }
}
