using System;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Tests.GpuCore;

internal static class FrameWorkload
{
    public const int RecordCount = 3335;
    public const int DistinctKeys = 400;
    public const int Seed = 1905;

    public static khaoz_draw_record[] Create()
    {
        var records = new khaoz_draw_record[RecordCount];
        for (int i = 0; i < records.Length; i++)
        {
            uint key = (uint)(i * DistinctKeys / RecordCount);
            var record = new khaoz_draw_record { mesh_id = (uint)i, material_id = key, sort_key = key };
            record.world[0] = 1;
            record.world[5] = 1;
            record.world[10] = 1;
            record.world[3] = i;
            records[i] = record;
        }

        return records;
    }

    public static void ShuffleRandom(Span<khaoz_draw_record> records, Random rng) => rng.Shuffle(records);

    public static void MoveCoherent(Span<khaoz_draw_record> records, Random rng)
    {
        if (records.Length < 2)
            return;

        for (int pair = 0; pair < 33; pair++)
        {
            int first = rng.Next(records.Length);
            int second = rng.Next(records.Length - 1);
            if (second >= first)
                second++;

            (records[first], records[second]) = (records[second], records[first]);
        }
    }
}
