using System;
using System.IO;
using SixLabors.ImageSharp;

namespace PixelLabSheetAssembler;

internal static class Program
{
    private const string Usage =
        "PixelLabSheetAssembler - assemble a PixelLab character export into a Direction8 grid sheet.\n" +
        "\n" +
        "Usage:\n" +
        "  dotnet run --project tools/PixelLabSheetAssembler -- \\\n" +
        "    --input <char.zip|dir> --anim <name> [--out <path.png>] \\\n" +
        "    [--fps <n>] [--bottom-pad <px>] [--alpha-threshold <0-255>] [--strict]\n";

    private static int Main(string[] args)
    {
        string? input = null, anim = null, outPath = null;
        float fps = 10f;
        int bottomPad = 0, alphaThreshold = 0;
        bool strict = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input": input = Next(args, ref i); break;
                    case "--anim": anim = Next(args, ref i); break;
                    case "--out": outPath = Next(args, ref i); break;
                    case "--fps": fps = float.Parse(Next(args, ref i)); break;
                    case "--bottom-pad": bottomPad = int.Parse(Next(args, ref i)); break;
                    case "--alpha-threshold": alphaThreshold = int.Parse(Next(args, ref i)); break;
                    case "--strict": strict = true; break;
                    case "-h":
                    case "--help": Console.WriteLine(Usage); return 0;
                    default: throw new ArgumentException($"unknown argument '{args[i]}'.");
                }
            }

            if (string.IsNullOrEmpty(input)) throw new ArgumentException("--input is required.");
            if (string.IsNullOrEmpty(anim)) throw new ArgumentException("--anim is required.");
            if (fps <= 0f) throw new ArgumentException("--fps must be positive.");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string? temp = null;
        try
        {
            var (charAnim, t) = PixelLabExport.Load(input!, anim!);
            temp = t;

            AssemblyResult result = SheetAssembler.Assemble(
                charAnim, new AssemblyOptions(bottomPad, alphaThreshold, strict, fps));

            outPath ??= DefaultOut(input!, anim!);
            using (result.Sheet)
            {
                result.Sheet.SaveAsPng(outPath);

                Console.WriteLine(
                    $"Wrote {outPath}  ({result.FrameCount}x{DirectionRows.RowCount} cells, " +
                    $"cell {result.CellWidth}x{result.CellHeight}, " +
                    $"sheet {result.Sheet.Width}x{result.Sheet.Height})");
            }
            Console.WriteLine($"frameCount = {result.FrameCount}");
            Console.WriteLine($"suggested fps = {result.SuggestedFps:0.#}");
            Console.WriteLine(
                $"FromGridSheet(sheet, frameCount: {result.FrameCount}, fps: {result.SuggestedFps:0.#}f)");
            foreach (string w in result.Warnings)
                Console.WriteLine(w);

            return 0;
        }
        catch (AssemblyException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            if (temp != null)
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value after '{args[i]}'.");
        return args[++i];
    }

    private static string DefaultOut(string input, string anim)
    {
        string dir = Directory.Exists(input) ? input : (Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".");
        string baseName = Directory.Exists(input)
            ? new DirectoryInfo(input).Name
            : Path.GetFileNameWithoutExtension(input);
        return Path.Combine(dir, $"{baseName}_{anim}.png");
    }
}
