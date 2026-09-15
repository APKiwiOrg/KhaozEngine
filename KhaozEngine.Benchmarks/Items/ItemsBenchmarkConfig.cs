using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The <c>--items</c> mode's arguments. It follows the journal set's shape: a record with a static
/// <c>Parse</c>, validated before anything is measured.
/// </summary>
public sealed record ItemsBenchmarkConfig
{
    public const int MaximumPlayers = 100_000;
    public const int MaximumGenerations = 20_000_000;
    public const int MaximumCrafts = 120;

    public bool Quick { get; init; }
    public int Players { get; init; } = 1_000;
    public int Generations { get; init; } = 1_000_000;
    public int Crafts { get; init; } = 20;
    public int Seed { get; init; } = 915;
    public string? DatabasePath { get; init; }
    public string? OutputPath { get; init; }

    /// <summary>Mods and bases are the owner's scale in both modes: budget 9 measures the table build at it.</summary>
    public int ModCount => 2_000;

    public int BaseCount => 50_000;

    /// <summary>The hot set a live server rolls from, which is what makes the 9.2 memo worth having.</summary>
    public int HotBaseCount => Quick ? 128 : 512;

    public int ThroughputSeconds => Quick ? 10 : 60;

    public int ScaleInstances => Quick ? 500_000 : 3_000_000;

    public int BankPagesPerPlayer => 10;

    public static ItemsBenchmarkConfig Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool quick = false;
        int players = 1_000;
        int generations = 1_000_000;
        int crafts = 20;
        int seed = 915;
        string? database = null;
        string? output = null;
        bool playersGiven = false;
        bool generationsGiven = false;

        for (int index = 0; index < args.Count; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--items":
                    break;
                case "--quick":
                    quick = true;
                    break;
                case "--players":
                    players = ParseInt(Value(args, ref index, option), option);
                    playersGiven = true;
                    break;
                case "--generations":
                    generations = ParseInt(Value(args, ref index, option), option);
                    generationsGiven = true;
                    break;
                case "--crafts":
                    crafts = ParseInt(Value(args, ref index, option), option);
                    break;
                case "--seed":
                    seed = ParseInt(Value(args, ref index, option), option);
                    break;
                case "--database":
                    database = Value(args, ref index, option);
                    break;
                case "--output":
                    output = Value(args, ref index, option);
                    break;
                default:
                    throw new ArgumentException($"Unknown items benchmark option '{option}'.", nameof(args));
            }
        }

        var config = new ItemsBenchmarkConfig
        {
            Quick = quick,
            Players = quick && !playersGiven ? 200 : players,
            Generations = quick && !generationsGiven ? 200_000 : generations,
            Crafts = crafts,
            Seed = seed,
            DatabasePath = database,
            OutputPath = output,
        };
        config.Validate();
        return config;
    }

    public void Validate()
    {
        InRange(Players, 1, MaximumPlayers, nameof(Players));
        InRange(Generations, 1_000, MaximumGenerations, nameof(Generations));
        InRange(Crafts, 1, MaximumCrafts, nameof(Crafts));
        if (DatabasePath is not null && !Path.IsPathFullyQualified(DatabasePath))
            throw new ArgumentException("Database path must be absolute.", nameof(DatabasePath));
        if (OutputPath is not null && !Path.IsPathFullyQualified(OutputPath))
            throw new ArgumentException("Output path must be absolute.", nameof(OutputPath));
        if (OutputPath is not null && !string.Equals(Path.GetExtension(OutputPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must use the .json extension.", nameof(OutputPath));
        if (DatabasePath is not null
            && OutputPath is not null
            && string.Equals(Path.GetFullPath(DatabasePath), Path.GetFullPath(OutputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Database and output paths must be different.");
    }

    private static string Value(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count) throw new ArgumentException($"Option '{option}' requires a value.", nameof(args));
        return args[index];
    }

    private static int ParseInt(string value, string option)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"Option '{option}' requires an invariant integer.", nameof(value));

    private static void InRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"Value must be from {minimum} through {maximum}.");
    }
}
