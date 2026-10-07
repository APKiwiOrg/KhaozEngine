using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed class PrivateOracleFailure(string message) : Exception(message);

[UnsupportedOSPlatform("windows")]
internal sealed class PrivateOracleReport : IDisposable
{
    internal const string SecureFailure = "private oracle: report could not be created securely";
    readonly string _path;
    readonly StreamWriter _writer;
    readonly Dictionary<string, int> _failed = new(StringComparer.Ordinal);
    readonly List<string> _failureOrder = new();
    bool _closed;

    PrivateOracleReport(string path, StreamWriter writer)
    {
        _path = path;
        _writer = writer;
    }

    internal static PrivateOracleReport Open(string path)
    {
        FileStream? stream = null;
        try
        {
            if (OperatingSystem.IsWindows()) throw new PrivateOracleFailure(SecureFailure);
            path = PrivateOraclePaths.CanonicalReportPath(path);
            if (new FileInfo(path).LinkTarget is not null) throw new PrivateOracleFailure(SecureFailure);
            string parent = Path.GetDirectoryName(path)!;
            const UnixFileMode parentMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (!Directory.Exists(parent) || File.GetUnixFileMode(parent) != parentMode)
                throw new PrivateOracleFailure(SecureFailure);
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                UnixCreateMode = fileMode,
            });
            if (File.GetUnixFileMode(path) != fileMode) throw new PrivateOracleFailure(SecureFailure);
            return new(path, new StreamWriter(stream, new UTF8Encoding(false)));
        }
        catch (Exception)
        {
            try { stream?.Dispose(); }
            catch (Exception) { }
            throw new PrivateOracleFailure(SecureFailure);
        }
    }

    internal void Record(string category, string detailJson)
    {
        if (_closed) throw new ObjectDisposedException(nameof(PrivateOracleReport));
        ValidateCategory(category);
        using JsonDocument detail = JsonDocument.Parse(detailJson);
        _writer.WriteLine(JsonSerializer.Serialize(new { category, detail = detail.RootElement }));
    }

    internal void Check(string category, bool ok, string detailJson)
    {
        if (ok) return;
        Record(category, detailJson);
        if (!_failed.TryGetValue(category, out int count)) _failureOrder.Add(category);
        _failed[category] = checked(count + 1);
    }

    internal void ThrowIfAnyFailed()
    {
        if (_failureOrder.Count == 0) return;
        string category = _failureOrder[0];
        Dispose();
        throw new PrivateOracleFailure($"private oracle: {_failed[category]} {category} mismatches, report sha256 {Sha256}");
    }

    internal string Sha256
    {
        get
        {
            try
            {
                if (!_closed) _writer.Flush();
                using FileStream stream = File.OpenRead(_path);
                return Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception) { throw new PrivateOracleFailure(SecureFailure); }
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        try { _writer.Dispose(); }
        catch (Exception) { throw new PrivateOracleFailure(SecureFailure); }
        finally { _closed = true; }
    }

    static void ValidateCategory(string category)
    {
        if (string.IsNullOrEmpty(category) || category.Length > 64)
            throw new ArgumentException("invalid report category");
        foreach (char c in category)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                throw new ArgumentException("invalid report category");
    }
}
