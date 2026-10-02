using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace KhaozEngine.App;

/// <summary>Parses simple env-file assignments without applying environment or search-path policy.</summary>
public static class EnvFile
{
    /// <summary>Reads assignments in order, preserving duplicate and case-distinct keys. Blank lines, whole-line
    /// # comments, lines without '=' and empty keys are ignored. Keys and values are trimmed, then matching outer
    /// single or double quotes are removed. Empty values, embedded equals and inline # text are retained. No
    /// interpolation, escaping, export-prefix processing or multiline values are supported. Leaves the reader open.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var entries = new List<KeyValuePair<string, string>>();
        while (reader.ReadLine() is string line)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            string key = line[..equals].Trim();
            if (key.Length == 0) continue;
            string value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            entries.Add(new KeyValuePair<string, string>(key, value));
        }
        return entries.AsReadOnly();
    }

    /// <summary>Reads one optional file with BCL byte-order-mark detection. Returns false and empty entries on
    /// expected path or file-access failures, including read failures. A successful result is published only after
    /// the complete file is read and closed. Null or empty paths are argument errors.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static bool TryRead(string path, out IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        entries = Array.Empty<KeyValuePair<string, string>>();
        StreamReader reader;
        try
        {
            reader = new StreamReader(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }

        try
        {
            IReadOnlyList<KeyValuePair<string, string>> parsed;
            using (reader)
                parsed = Parse(reader);
            entries = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
