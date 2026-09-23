using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct ResidentMeasurement(
    long PageBytesSum,
    long PageResidentBytes,
    long AdmittedResidentBytes,
    long TotalResidentBytes,
    long TotalMemoryDeltaBytes,
    string AdmittedStatus);

internal readonly record struct ScaleMeasurement(
    int Instances,
    int PageCount,
    long PageBytesSum,
    long ResidentBytes,
    long TotalMemoryDeltaBytes,
    double BytesPerInstance);

/// <summary>
/// Budget 12 and the scale test of the spec's test plan row 5. Both are resident-memory questions, so
/// both are a <see cref="ResidentMemory.Read"/> delta across a build that keeps every byte it produced,
/// with the <c>GC.GetTotalMemory(true)</c> delta beside it.
/// </summary>
internal static class ItemsMemoryMeasurements
{
    /// <summary>
    /// Budget 12: the encoded pages of a 1,000 stack affixed bank for every logged-in player, and then
    /// the same pages again through the admitted layer, which holds a committed and an uncommitted
    /// section dictionary per stream and clones every byte array on the way in.
    /// </summary>
    internal static ResidentMeasurement MeasureResident(
        GeneratedRares pool,
        InstanceIdAllocator allocator,
        int players,
        int pagesPerPlayer,
        int contentVersion)
    {
        var entries = new PageSlotInput[ContainerPageCodec.ContainerPageSlots];
        int cursor = 0;
        long totalMemoryBefore = ResidentMemory.ReadTotalMemory();
        long before = ResidentMemory.Read();
        var pages = new byte[players][][];
        long pageBytesSum = 0;
        for (int player = 0; player < players; player++)
        {
            pages[player] = new byte[pagesPerPlayer][];
            for (int page = 0; page < pagesPerPlayer; page++)
            {
                pages[player][page] = BuildPage(pool, allocator, entries, page, contentVersion, ref cursor);
                pageBytesSum += pages[player][page].Length;
            }
        }

        long afterPages = ResidentMemory.Read();
        long totalMemoryAfter = ResidentMemory.ReadTotalMemory();
        string admittedStatus;
        long afterAdmitted;
        var store = new InMemoryMutationJournalStore(JournalLimits.Maximum, TimeSpan.FromHours(1), TimeProvider.System);
        var executor = new MutationJournalExecutor(store, new JournalExecutorOptions(1, 16, 4L * 1024 * 1024));
        try
        {
            var sections = new JournalProjectionSection[pagesPerPlayer];
            DateTimeOffset stamp = DateTimeOffset.UnixEpoch;
            for (int player = 0; player < players; player++)
            {
                string stream = $"items/resident/player{player:D6}";
                for (int page = 0; page < pagesPerPlayer; page++)
                    sections[page] = new JournalProjectionSection(
                        stream,
                        ContainerPageCodec.SectionName("bank", page),
                        1,
                        ItemsCommitFactory.PageSchema,
                        1,
                        pages[player][page],
                        stamp);
                executor.SeedCommitted(stream, 1, sections);
            }

            afterAdmitted = ResidentMemory.Read();
            admittedStatus = "seeded";
        }
        finally
        {
            executor.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }

        long pageBytes = afterPages - before;
        long admittedBytes = afterAdmitted - afterPages;
        GC.KeepAlive(pages);
        return new ResidentMeasurement(
            pageBytesSum,
            pageBytes,
            admittedBytes,
            pageBytes + admittedBytes,
            totalMemoryAfter - totalMemoryBefore,
            admittedStatus);
    }

    /// <summary>
    /// The scale test row 5 names: several million instances in memory as what they actually are, which
    /// is payload bytes plus slot entries inside page blobs rather than an object per item.
    /// </summary>
    internal static ScaleMeasurement MeasureScale(
        GeneratedRares pool,
        InstanceIdAllocator allocator,
        int instances,
        int contentVersion)
    {
        int pageCount = instances / ContainerPageCodec.ContainerPageSlots;
        var entries = new PageSlotInput[ContainerPageCodec.ContainerPageSlots];
        int cursor = 0;
        long totalMemoryBefore = ResidentMemory.ReadTotalMemory();
        long before = ResidentMemory.Read();
        var pages = new byte[pageCount][];
        long pageBytesSum = 0;
        for (int page = 0; page < pageCount; page++)
        {
            pages[page] = BuildPage(pool, allocator, entries, page % 100, contentVersion, ref cursor);
            pageBytesSum += pages[page].Length;
        }

        long after = ResidentMemory.Read();
        long totalMemoryAfter = ResidentMemory.ReadTotalMemory();
        long resident = after - before;
        GC.KeepAlive(pages);
        return new ScaleMeasurement(
            pageCount * ContainerPageCodec.ContainerPageSlots,
            pageCount,
            pageBytesSum,
            resident,
            totalMemoryAfter - totalMemoryBefore,
            pageCount == 0 ? 0 : (double)resident / (pageCount * ContainerPageCodec.ContainerPageSlots));
    }

    private static byte[] BuildPage(
        GeneratedRares pool,
        InstanceIdAllocator allocator,
        PageSlotInput[] entries,
        int pageIndex,
        int contentVersion,
        ref int cursor)
    {
        int firstSlot = pageIndex * ContainerPageCodec.ContainerPageSlots;
        for (int slot = 0; slot < entries.Length; slot++)
        {
            int index = cursor++ % pool.Count;
            entries[slot] = new PageSlotInput(
                firstSlot + slot,
                0,
                pool.BaseIds[index],
                1,
                (ulong)allocator.Next(),
                pool.Payloads[index]);
        }

        int size = ContainerPageCodec.EncodedSize(pageIndex, firstSlot, ContainerPageCodec.ContainerPageSlots, contentVersion, entries);
        var page = new byte[size];
        ContainerPageCodec.Encode(page, pageIndex, firstSlot, ContainerPageCodec.ContainerPageSlots, contentVersion, entries);
        return page;
    }
}
