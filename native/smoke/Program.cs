using System;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.NativeSmoke;

// Loads khaoz_native from the KhaozEngine.Native package the way a game would, then proves the three
// contracts a consumer meets first: the ABI gate, a frame-sized submit and a headless device that
// either works or refuses with the Vulkan-unavailable result. The exit code is the verdict.
internal static unsafe class Program
{
    private const int RecordCount = 3335;
    private const int DistinctKeys = 400;
    private const int Seed = 1905;

    private static int Main()
    {
        NativeLoadStatus status = NativeLibraryGate.Probe();
        Console.WriteLine($"probe: {status}");
        if (status != NativeLoadStatus.Ready)
            return 1;

        if (!FrameSortsKeys())
            return 1;

        return DeviceCreatesOrRefuses() ? 0 : 1;
    }

    private static bool FrameSortsKeys()
    {
        var records = new khaoz_draw_record[RecordCount];
        var expected = new ulong[RecordCount];
        for (int i = 0; i < records.Length; i++)
        {
            ulong key = (ulong)(i * DistinctKeys / RecordCount);
            records[i] = new khaoz_draw_record { mesh_id = (uint)i, material_id = (uint)key, sort_key = key };
            expected[i] = key;
        }

        new Random(Seed).Shuffle(records.AsSpan());

        khaoz_frame* frame = null;
        khaoz_result result = KhaozNative.khaoz_frame_create(RecordCount, &frame);
        if (result != khaoz_result.KHAOZ_OK)
        {
            Console.WriteLine($"frame create: {result}");
            return false;
        }

        try
        {
            var keys = new ulong[RecordCount];
            uint count = 0;
            fixed (khaoz_draw_record* source = records)
                result = KhaozNative.khaoz_frame_submit(frame, source, RecordCount);
            if (result == khaoz_result.KHAOZ_OK)
            {
                fixed (ulong* destination = keys)
                    result = KhaozNative.khaoz_frame_sorted_keys(frame, destination, RecordCount, &count);
            }

            bool sorted = result == khaoz_result.KHAOZ_OK && count == RecordCount
                && keys.AsSpan().SequenceEqual(expected);
            Console.WriteLine($"frame submit: {result}, {count} keys, sorted {sorted}");
            return sorted;
        }
        finally
        {
            KhaozNative.khaoz_frame_destroy(frame);
        }
    }

    private static bool DeviceCreatesOrRefuses()
    {
        // Validation needs the Khronos layer installed, so it follows the switch the native tests use.
        var desc = new khaoz_gpu_device_desc
        {
            enable_validation = Environment.GetEnvironmentVariable("KHAOZ_NATIVE_VALIDATION") == "1" ? 1u : 0u,
        };
        khaoz_gpu_device* device = null;
        khaoz_result result = KhaozNative.khaoz_gpu_device_create_headless(&desc, &device);
        if (result == khaoz_result.KHAOZ_ERROR_VULKAN_UNAVAILABLE)
        {
            Console.WriteLine("device: Vulkan unavailable, refused cleanly");
            return true;
        }

        if (result != khaoz_result.KHAOZ_OK)
        {
            Console.WriteLine($"device create: {result}");
            return false;
        }

        uint errors = 0;
        result = KhaozNative.khaoz_gpu_device_destroy(device, &errors);
        Console.WriteLine($"device: created, validation {desc.enable_validation}, destroy {result}, {errors} validation errors");
        return result == khaoz_result.KHAOZ_OK && errors == 0;
    }
}
