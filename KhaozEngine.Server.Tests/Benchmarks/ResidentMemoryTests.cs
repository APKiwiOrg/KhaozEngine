using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Benchmarks.Items;
using Xunit;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>
/// The instrument every memory budget is read through, fenced on its own rather than only through the
/// budgets that consume it.
/// <para>
/// <b>What this pins is the size of a retention, within the instrument's own noise.</b> Retaining a known
/// live set has to move the reading by that set, because a retention instrument that reads holding memory
/// as nothing, or as a release, cannot judge a budget. The band is the same four mebibytes the
/// two-readings fact allows, which is far more than a settled test host drifts and far less than the live
/// set.
/// </para>
/// <para>
/// The bugs they exist for. <c>Read</c> called <c>GetGCMemoryInfo()</c> with no argument, which reports the
/// latest collection OF ANY KIND rather than the forced blocking one it had just performed
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1030). Then it returned <c>HeapSizeBytes</c>, which
/// counts the free gaps a swept large object heap keeps. A full suite leaves plenty of those, so a new
/// large array fitted into one without the heap growing, and 16 MiB retained here read as a delta of 0
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1043). A region whose last survivor went between the
/// readings took its gaps with it, which is how budget 12 read a retention as tens of megabytes released
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1018). The two gap facts below build each state on
/// purpose instead of waiting for a suite to leave it behind, and both went red on the old reading.
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public sealed class ResidentMemoryTests
{
    /// <summary>Sixteen mebibytes in one mebibyte blocks, big enough to dominate the surrounding noise and
    /// small enough that forcing three compacting collections around it stays cheap.</summary>
    const int BlockBytes = 1024 * 1024;
    const int BlockCount = 16;
    const long LiveBytes = (long)BlockBytes * BlockCount;

    /// <summary>What two readings may disagree by with nothing retained between them.</summary>
    const long NoiseBytes = 4L * BlockBytes;

    [Fact]
    public void Retaining_a_known_live_set_moves_the_reading_up()
    {
        long before = ResidentMemory.Read();
        List<byte[]> held = RetainLargeBlocks();
        long after = ResidentMemory.Read();
        GC.KeepAlive(held);

        AssertMovedBy(LiveBytes, before, after);
    }

    [Fact]
    public void Free_large_object_heap_space_left_by_earlier_work_does_not_absorb_a_retention()
    {
        // Earlier work: 48 dead one mebibyte arrays behind one survivor, which keeps their region and
        // leaves 48 MiB of free gaps in it. A compacting collection does not move the large object heap,
        // so the gaps are still there when the reading is taken, and the 16 blocks below fit inside them.
        var survivor = new byte[1][];
        LeaveLargeObjectGaps(48, survivor);

        long before = ResidentMemory.Read();
        List<byte[]> held = RetainLargeBlocks();
        long after = ResidentMemory.Read();
        GC.KeepAlive(held);
        GC.KeepAlive(survivor);

        AssertMovedBy(LiveBytes, before, after);
    }

    [Fact]
    public void A_region_the_collector_hands_back_between_the_readings_does_not_turn_a_retention_negative()
    {
        // Earlier work leaves 32 MiB of gaps behind one survivor, and lets the survivor go between the two
        // readings, so the whole region empties and goes back to the collector. The retention is small
        // object pages, which never land in that region. The survivor's own mebibyte really was released,
        // so the reading may drop by it and no more.
        var holder = new byte[1][];
        LeaveLargeObjectGaps(32, holder);

        long before = ResidentMemory.Read();
        holder[0] = null!;
        var pages = new byte[2_048][];
        for (int page = 0; page < pages.Length; page++) pages[page] = new byte[BlockBytes / 128];
        long after = ResidentMemory.Read();
        GC.KeepAlive(pages);

        AssertMovedBy(LiveBytes - BlockBytes, before, after);
    }

    [Fact]
    public void Two_readings_with_nothing_retained_between_them_agree()
    {
        long first = ResidentMemory.Read();
        long second = ResidentMemory.Read();

        Assert.InRange(Math.Abs(second - first), 0L, NoiseBytes);
    }

    static List<byte[]> RetainLargeBlocks()
    {
        var held = new List<byte[]>(BlockCount);
        for (int i = 0; i < BlockCount; i++)
        {
            var block = new byte[BlockBytes];
            // Touch every page so the block cannot be an untouched reservation on any runtime.
            for (int b = 0; b < block.Length; b += 4096) block[b] = (byte)i;
            held.Add(block);
        }

        return held;
    }

    /// <summary>Allocates <paramref name="deadBlocks"/> one mebibyte arrays and one more after them, and
    /// keeps only the last, in <paramref name="survivor"/>. Out of line and written through the caller's
    /// array rather than returned, so no frame and no spilled temporary of the caller holds any of them and
    /// clearing the slot really does release the survivor.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void LeaveLargeObjectGaps(int deadBlocks, byte[][] survivor)
    {
        var blocks = new byte[deadBlocks + 1][];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = new byte[BlockBytes];
        survivor[0] = blocks[deadBlocks];
    }

    static void AssertMovedBy(long expected, long before, long after)
    {
        long delta = after - before;
        Assert.True(
            Math.Abs(delta - expected) <= NoiseBytes,
            $"retaining {expected} bytes read as a delta of {delta} bytes ({before} -> {after}), " +
            $"outside the {NoiseBytes} byte noise band");
    }
}
