using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// Which measurement phases a run performs. A full run is every phase, and the split exists so the
/// stress figure can be measured in pieces when one process would run past a harness timeout.
/// </summary>
[Flags]
public enum CatalogPhases
{
    None = 0,
    Publish = 1 << 0,   // P1, P2: encode every chunk, write the pack, both manifests
    Edit = 1 << 1,      // P5, P6: one-item edit, rebuild only the affected chunk
    Load = 1 << 2,      // P3, P7, P8, P9: server runtime load, lookup, validator, loot draw
    Text = 1 << 3,      // P10: text chunk decode for one language
    Fetch = 1 << 4,     // P4, P4b: client cold start over the shaped link
    Compose = 1 << 5,   // P11: composed cold boot with a stand-in per-type load index
    All = Publish | Edit | Load | Text | Fetch | Compose,
}

/// <summary>
/// The <c>--catalog</c> mode's parsed arguments. Section 14.2 of the content catalog design names the
/// flags: <c>--definitions</c>, <c>--types</c>, <c>--chunk-slots</c>, <c>--languages</c>,
/// <c>--edit-count</c> and <c>--compose</c>, plus the harness-wide <c>--quick</c>, <c>--output</c> and
/// <c>--seed</c>.
/// </summary>
public sealed record CatalogBenchmarkConfig
{
    public const int MaximumDefinitions = 4_000_000;
    public const int MaximumTypes = 64;
    public const int MaximumLanguages = 16;
    public const int EngineTypeCount = 6;

    /// <summary>Item definitions to synthesize. The two owner figures are 50,000 and 1,000,000.</summary>
    public int Definitions { get; init; } = 50_000;

    /// <summary>Registered content types in total. Six are the engine's, the rest are synthetic game types.</summary>
    public int Types { get; init; } = EngineTypeCount;

    /// <summary>The <c>item</c> type's chunk slot count. 1,024 is section 3.1's registration.</summary>
    public int ChunkSlots { get; init; } = 1_024;

    /// <summary>Languages to emit text chunks for. P1 and P2 are stated at one language.</summary>
    public int Languages { get; init; } = 1;

    /// <summary>Item rows the P5 and P6 edit touches. One is the budget's own case.</summary>
    public int EditCount { get; init; } = 1;

    /// <summary>P11's flag: boot once with every load index built and time from process start.</summary>
    public bool Compose { get; init; }

    /// <summary>A smoke run at a small definition count, for a sub-minute sanity check.</summary>
    public bool Quick { get; init; }

    public int Seed { get; init; } = 835;

    /// <summary>Absolute path of the JSON result file, or null to print only.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Absolute path of the pack store root, or null for a temporary directory removed after.</summary>
    public string? PackRoot { get; init; }

    /// <summary>The shaped link the client fetch loop runs over, in bits per second.</summary>
    public long LinkBitsPerSecond { get; init; } = 20_000_000;

    /// <summary>Bounded concurrency of the client fetch loop (section 8.7).</summary>
    public int FetchConcurrency { get; init; } = 4;

    public CatalogPhases Phases { get; init; } = CatalogPhases.All;

    /// <summary>The comparison chunk slot count P6 also reports, answering the spec's question Q3.</summary>
    public int ComparisonChunkSlots { get; init; } = 4_096;

    public static CatalogBenchmarkConfig Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool quick = false;
        bool compose = false;
        int definitions = 50_000;
        int types = EngineTypeCount;
        int chunkSlots = 1_024;
        int comparisonChunkSlots = 4_096;
        int languages = 1;
        int editCount = 1;
        int seed = 835;
        int concurrency = 4;
        long linkBits = 20_000_000;
        string? output = null;
        string? packRoot = null;
        CatalogPhases? phases = null;
        bool definitionsGiven = false;

        for (int i = 0; i < args.Count; i++)
        {
            string option = args[i];
            switch (option)
            {
                case "--catalog":
                    break;
                case "--definitions":
                    definitions = ParseInt(Value(args, ref i, option), option);
                    definitionsGiven = true;
                    break;
                case "--types":
                    types = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--chunk-slots":
                    chunkSlots = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--comparison-chunk-slots":
                    comparisonChunkSlots = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--languages":
                    languages = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--edit-count":
                    editCount = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--compose":
                    compose = true;
                    break;
                case "--quick":
                    quick = true;
                    break;
                case "--seed":
                    seed = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--output":
                    output = Value(args, ref i, option);
                    break;
                case "--pack-root":
                    packRoot = Value(args, ref i, option);
                    break;
                case "--link-mbit":
                    linkBits = ParseInt(Value(args, ref i, option), option) * 1_000_000L;
                    break;
                case "--fetch-concurrency":
                    concurrency = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--phases":
                    phases = ParsePhases(Value(args, ref i, option));
                    break;
                default:
                    throw new ArgumentException($"Unknown catalog benchmark option '{option}'.", nameof(args));
            }
        }

        if (quick && !definitionsGiven) definitions = 5_000;

        var config = new CatalogBenchmarkConfig
        {
            Definitions = definitions,
            Types = types,
            ChunkSlots = chunkSlots,
            ComparisonChunkSlots = comparisonChunkSlots,
            Languages = languages,
            EditCount = editCount,
            Compose = compose,
            Quick = quick,
            Seed = seed,
            OutputPath = output,
            PackRoot = packRoot,
            LinkBitsPerSecond = linkBits,
            FetchConcurrency = concurrency,
            Phases = phases ?? (compose ? CatalogPhases.Compose : CatalogPhases.All),
        };
        config.Validate();
        return config;
    }

    public void Validate()
    {
        InRange(Definitions, 512, MaximumDefinitions, nameof(Definitions));
        InRange(Types, EngineTypeCount, MaximumTypes, nameof(Types));
        InRange(Languages, 1, MaximumLanguages, nameof(Languages));
        InRange(EditCount, 1, 4_096, nameof(EditCount));
        InRange(FetchConcurrency, 1, 64, nameof(FetchConcurrency));
        PowerOfTwoSlots(ChunkSlots, nameof(ChunkSlots));
        PowerOfTwoSlots(ComparisonChunkSlots, nameof(ComparisonChunkSlots));
        if (LinkBitsPerSecond < 1_000_000 || LinkBitsPerSecond > 10_000_000_000L)
            throw new ArgumentOutOfRangeException(nameof(LinkBitsPerSecond), LinkBitsPerSecond, "Link rate must be between 1 Mbit and 10 Gbit.");
        if (Phases == CatalogPhases.None)
            throw new ArgumentException("At least one phase must run.", nameof(Phases));
    }

    /// <summary>The dense id range, leaving room for the sparse family blocks at the top of the space.</summary>
    public int DenseDefinitions => Definitions - (FamilyCount * FamilyBlockMembers);

    /// <summary>Families exist so a few chunks are sparse, which is what a family block does to the id space.</summary>
    public int FamilyCount => 4;

    public int FamilyBlockSize => 65_536;

    public int FamilyBlockMembers => 40;

    public int TagCount => 200;

    public int StatCount => 300;

    /// <summary>Four loot entries per ten item definitions (section 14.1's second largest type).</summary>
    public int LootEntryCount => (int)((long)Definitions * 4 / 10);

    public int LootTableCount => Math.Max(1, LootEntryCount / 4);

    /// <summary>Base sockets on a third of the item bases, one authored socket each.</summary>
    public int BaseSocketCount => Definitions / 3;

    public int GameTypeCount => Types - EngineTypeCount;

    public int GameTypeRowCount => 256;

    private static CatalogPhases ParsePhases(string value)
    {
        CatalogPhases result = CatalogPhases.None;
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= part.ToLowerInvariant() switch
            {
                "all" => CatalogPhases.All,
                "publish" => CatalogPhases.Publish,
                "edit" => CatalogPhases.Edit,
                "load" => CatalogPhases.Load,
                "text" => CatalogPhases.Text,
                "fetch" => CatalogPhases.Fetch,
                "compose" => CatalogPhases.Compose,
                _ => throw new ArgumentException($"Unknown catalog phase '{part}'.", nameof(value)),
            };
        }
        return result;
    }

    private static string Value(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count) throw new ArgumentException($"Option '{option}' requires a value.", nameof(args));
        index++;
        return args[index];
    }

    private static int ParseInt(string text, string option) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ArgumentException($"Option '{option}' requires an integer, got '{text}'.", nameof(text));

    private static void InRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {minimum} and {maximum}.");
    }

    private static void PowerOfTwoSlots(int value, string name)
    {
        if (value < 256 || value > 65_536 || (value & (value - 1)) != 0)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be a power of two between 256 and 65536.");
    }
}
