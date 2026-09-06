using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace KhaozEngine.Benchmarks.Journal;

public enum JournalBenchmarkMode
{
    Benchmark,
    Soak,
}

public sealed record JournalBenchmarkConfig
{
    public const int MaximumOperations = 10_000_000;
    public const int MaximumPlayers = 1_000_000;
    public const int MaximumWorkers = 128;
    public const int MaximumPayloadBytes = 4_096;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(24);

    public JournalBenchmarkMode Mode { get; init; } = JournalBenchmarkMode.Benchmark;
    public int Operations { get; init; } = 10_000;
    public int Players { get; init; } = 1_000;
    public int Seed { get; init; } = 835;
    public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public int PayloadBytes { get; init; } = 192;
    public string? DatabasePath { get; init; }
    public string? SqlServerEnvironmentVariable { get; init; }
    public string? OutputPath { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(10);

    public static JournalBenchmarkConfig Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        JournalBenchmarkMode? mode = null;
        int operations = 10_000;
        int players = 1_000;
        int seed = 835;
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        int payloadBytes = 192;
        string? database = null;
        string? sqlServerEnvironmentVariable = null;
        string? output = null;
        TimeSpan duration = TimeSpan.FromMinutes(5);
        TimeSpan progress = TimeSpan.FromSeconds(10);

        for (int i = 0; i < args.Count; i++)
        {
            string option = args[i];
            switch (option)
            {
                case "--journal":
                    SetMode(ref mode, JournalBenchmarkMode.Benchmark);
                    break;
                case "--journal-soak":
                    SetMode(ref mode, JournalBenchmarkMode.Soak);
                    break;
                case "--operations":
                    operations = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--players":
                    players = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--seed":
                    seed = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--workers":
                    workers = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--payload-bytes":
                    payloadBytes = ParseInt(Value(args, ref i, option), option);
                    break;
                case "--database":
                    database = Value(args, ref i, option);
                    break;
                case "--sql-server-env":
                    sqlServerEnvironmentVariable = Value(args, ref i, option);
                    break;
                case "--output":
                    output = Value(args, ref i, option);
                    break;
                case "--duration-seconds":
                    duration = TimeSpan.FromSeconds(ParseInt(Value(args, ref i, option), option));
                    break;
                case "--progress-seconds":
                    progress = TimeSpan.FromSeconds(ParseInt(Value(args, ref i, option), option));
                    break;
                default:
                    throw new ArgumentException($"Unknown journal benchmark option '{option}'.", nameof(args));
            }
        }

        var config = new JournalBenchmarkConfig
        {
            Mode = mode ?? throw new ArgumentException("Specify --journal or --journal-soak.", nameof(args)),
            Operations = operations,
            Players = players,
            Seed = seed,
            Workers = workers,
            PayloadBytes = payloadBytes,
            DatabasePath = database,
            SqlServerEnvironmentVariable = sqlServerEnvironmentVariable,
            OutputPath = output,
            Duration = duration,
            ProgressInterval = progress,
        };
        config.Validate();
        return config;
    }

    public void Validate()
    {
        InRange(Operations, 7, MaximumOperations, nameof(Operations));
        InRange(Players, 2, MaximumPlayers, nameof(Players));
        InRange(Workers, 1, MaximumWorkers, nameof(Workers));
        InRange(PayloadBytes, 1, MaximumPayloadBytes, nameof(PayloadBytes));
        if (Duration <= TimeSpan.Zero || Duration > MaximumDuration)
            throw new ArgumentOutOfRangeException(nameof(Duration), Duration, $"Duration must be positive and no greater than {MaximumDuration}.");
        if (ProgressInterval <= TimeSpan.Zero || ProgressInterval > Duration)
            throw new ArgumentOutOfRangeException(nameof(ProgressInterval), ProgressInterval, "Progress interval must be positive and no greater than duration.");
        if (DatabasePath is not null && !Path.IsPathFullyQualified(DatabasePath))
            throw new ArgumentException("Database path must be absolute.", nameof(DatabasePath));
        if (DatabasePath is not null && SqlServerEnvironmentVariable is not null)
            throw new ArgumentException("Choose SQLite or SQL Server, not both.");
        if (SqlServerEnvironmentVariable is not null && !IsEnvironmentVariableName(SqlServerEnvironmentVariable))
            throw new ArgumentException("SQL Server environment variable name is invalid.", nameof(SqlServerEnvironmentVariable));
        if (OutputPath is not null && !Path.IsPathFullyQualified(OutputPath))
            throw new ArgumentException("Output path must be absolute.", nameof(OutputPath));
        if (OutputPath is not null && !string.Equals(Path.GetExtension(OutputPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must use the .json extension.", nameof(OutputPath));
        if (DatabasePath is not null
            && OutputPath is not null
            && string.Equals(Path.GetFullPath(DatabasePath), Path.GetFullPath(OutputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Database and output paths must be different.");
    }

    private static void SetMode(ref JournalBenchmarkMode? mode, JournalBenchmarkMode value)
    {
        if (mode is not null) throw new ArgumentException("Specify exactly one journal benchmark mode.");
        mode = value;
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

    private static bool IsEnvironmentVariableName(string value)
    {
        if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_')) return false;
        for (int i = 1; i < value.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(value[i]) || value[i] == '_')) return false;
        return true;
    }
}
