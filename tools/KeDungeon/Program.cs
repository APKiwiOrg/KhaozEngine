using System;
using System.Globalization;
using System.IO;
using KhaozEngine.Dungeon;
using KhaozEngine.Imaging;
using KhaozEngine.MapDoc;

namespace KeDungeon;

/// <summary>
/// ke-dungeon: a dev CLI over <c>KhaozEngine.Dungeon</c>. Four verbs: <c>generate</c> a layout from a seed
/// (and optional config), <c>preview</c> a layout as one PNG per floor, <c>verify</c> a layout's
/// solvability, and <c>bake</c> a layout into a greybox <c>MapDoc</c> zone document at a world placement.
/// Dev tooling only: no localization requirements apply, output goes straight to the console.
/// </summary>
public static class Program
{
    const string Usage =
        "usage: ke-dungeon <verb> [options]\n" +
        "  generate --seed <ulong> [--config <config.json>] --out <layout.json>\n" +
        "  preview --layout <layout.json> --out-dir <dir>\n" +
        "  verify --layout <layout.json>\n" +
        "  bake --layout <layout.json> --map <map.json> --origin-x <f> --origin-z <f> --base-y <f> [--yaw <rad>]";

    /// <summary>Dispatches to the requested verb and returns the process exit code: 0 on success, 1 for a
    /// failed <c>verify</c>, 2 for an unknown verb or a missing/invalid required option (usage is printed
    /// in that case), 3 for malformed input JSON (a config, layout, or map document that fails to parse or
    /// validate, reported by message instead of a stack trace).</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return PrintUsageAndFail();
        }

        string verb = args[0];
        string[] rest = args[1..];

        try
        {
            return verb switch
            {
                "generate" => RunGenerate(rest),
                "preview" => RunPreview(rest),
                "verify" => RunVerify(rest),
                "bake" => RunBake(rest),
                _ => PrintUsageAndFail(),
            };
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (DungeonJsonException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (MapDocumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    static int PrintUsageAndFail()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    static int RunGenerate(string[] args)
    {
        string seedText = RequireOption(args, "--seed");
        if (!ulong.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong seed))
        {
            throw new CliUsageException($"--seed must be a non-negative integer, got '{seedText}'");
        }

        string? configPath = GetOption(args, "--config");
        string outPath = RequireOption(args, "--out");

        DungeonConfig config = configPath is null
            ? new DungeonConfig()
            : DungeonJson.LoadConfig(File.ReadAllText(configPath));

        DungeonLayout layout = DungeonGenerator.Generate(config, seed);

        WriteAllTextEnsuringDirectory(outPath, DungeonJson.SaveLayout(layout));

        PrintStats(layout.Stats);
        return 0;
    }

    static void PrintStats(LayoutStats stats)
    {
        Console.WriteLine($"roomsRequested: {stats.RoomsRequested}");
        Console.WriteLine($"roomsPlaced: {stats.RoomsPlaced}");
        Console.WriteLine($"criticalPathLength: {stats.CriticalPathLength}");
        Console.WriteLine($"floorsUsed: {stats.FloorsUsed}");
        Console.WriteLine($"locksRequested: {stats.LocksRequested}");
        Console.WriteLine($"locksPlaced: {stats.LocksPlaced}");
        Console.WriteLine($"saturated: {(stats.Saturated ? "true" : "false")}");
    }

    static int RunPreview(string[] args)
    {
        string layoutPath = RequireOption(args, "--layout");
        string outDir = RequireOption(args, "--out-dir");

        DungeonLayout layout = DungeonJson.LoadLayout(File.ReadAllText(layoutPath));

        string fullOutDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(fullOutDir);

        for (int floor = 0; floor < layout.Floors; floor++)
        {
            byte[] rgba = PreviewRenderer.RenderFloorRgba(layout, floor, out int width, out int height);
            string path = Path.Combine(fullOutDir, $"floor-{floor}.png");
            PngWriter.Save(path, rgba, width, height);
            Console.WriteLine(path);
        }

        return 0;
    }

    static int RunVerify(string[] args)
    {
        string layoutPath = RequireOption(args, "--layout");
        DungeonLayout layout = DungeonJson.LoadLayout(File.ReadAllText(layoutPath));

        DungeonSolveReport report = DungeonSolver.Verify(layout);
        if (report.IsSolvable)
        {
            return 0;
        }

        foreach (string error in report.Errors)
        {
            Console.Error.WriteLine(error);
        }

        return 1;
    }

    static int RunBake(string[] args)
    {
        string layoutPath = RequireOption(args, "--layout");
        string mapPath = RequireOption(args, "--map");
        float originX = ParseFloat(RequireOption(args, "--origin-x"), "--origin-x");
        float originZ = ParseFloat(RequireOption(args, "--origin-z"), "--origin-z");
        float baseY = ParseFloat(RequireOption(args, "--base-y"), "--base-y");

        string? yawText = GetOption(args, "--yaw");
        float yaw = yawText is null ? 0f : ParseFloat(yawText, "--yaw");

        DungeonLayout layout = DungeonJson.LoadLayout(File.ReadAllText(layoutPath));
        DungeonKitMap kit = DungeonKitMap.Greybox();
        var plot = new DungeonPlotTransform(originX, originZ, baseY, yaw);

        MapDocument target = LoadOrCreateMapDocument(mapPath);
        DungeonMapDocEmitter.Emit(layout, kit, plot, target);

        string fullMapPath = Path.GetFullPath(mapPath);
        string? dir = Path.GetDirectoryName(fullMapPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        MapDocumentFile.Save(target, fullMapPath);
        Console.WriteLine(fullMapPath);
        return 0;
    }

    // Loads the target document if it already exists (a dungeon bake can accumulate alongside
    // hand-authored content per DungeonMapDocEmitter.Emit's append-only contract), otherwise creates a
    // fresh one. A fresh document needs a non-empty Id to pass MapDocumentValidator. Bounds start at the
    // default (0,0,0,0) and Emit's bounds expansion always leaves MaxX > MinX / MaxZ > MinZ for a
    // non-degenerate plot, so the document saves cleanly without any bounds pre-seeding.
    static MapDocument LoadOrCreateMapDocument(string mapPath)
    {
        if (File.Exists(mapPath))
        {
            return MapDocumentFile.Load(mapPath);
        }

        string id = Path.GetFileNameWithoutExtension(mapPath);
        return new MapDocument { Id = id, DisplayName = id };
    }

    static void WriteAllTextEnsuringDirectory(string path, string contents)
    {
        string fullPath = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, contents);
    }

    static float ParseFloat(string text, string optionName)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            throw new CliUsageException($"{optionName} must be a number, got '{text}'");
        }

        return value;
    }

    static string RequireOption(string[] args, string name)
    {
        return GetOption(args, name) ?? throw new CliUsageException($"missing required option {name}");
    }

    static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    sealed class CliUsageException : Exception
    {
        public CliUsageException(string message) : base(message)
        {
        }
    }
}
