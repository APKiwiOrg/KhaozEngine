using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// The handshake file and the per-run token, gate 3's readable half. One file, written once when the host
    /// starts and deleted when it stops, carrying everything a bridge needs to connect: the ephemeral port, the
    /// token, the process id and the start time.
    /// <para>
    /// Be honest about what the token is for. It is not what protects players (the head's Debug-only reference is),
    /// and a loopback bind is reachable by every process on the developer's machine. The token raises the bar from
    /// "any local process can drive the client" to "any local process that can also read the developer's app data
    /// directory", which is worth the few lines it costs.
    /// </para>
    /// </summary>
    public static class AutomationHandshake
    {
        /// <summary>Token entropy in bytes. 32 bytes is 256 bits, comfortably past the 128-bit floor.</summary>
        public const int TokenBytes = 32;

        /// <summary>
        /// Mint a fresh per-run token: <see cref="TokenBytes"/> cryptographically random bytes in base64url (the
        /// URL and filename safe alphabet, unpadded), so the value survives a JSON string and a command line intact.
        /// </summary>
        public static string NewToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(TokenBytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Constant-time token comparison, so a caller cannot walk the token out one byte at a time off the reply
        /// latency. A length mismatch short-circuits, which leaks only the length.
        /// </summary>
        public static bool TokenMatches(string? expected, string? presented)
        {
            if (expected is null || presented is null) return false;
            byte[] a = Encoding.UTF8.GetBytes(expected);
            byte[] b = Encoding.UTF8.GetBytes(presented);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        /// <summary>The JSON body of the handshake file: port, token, pid and the ISO-8601 UTC start time.</summary>
        public static string Serialize(int port, string token, int processId, DateTimeOffset startedAt) =>
            new JsonObject
            {
                ["port"] = port,
                ["token"] = token,
                ["pid"] = processId,
                ["startedAt"] = startedAt.ToUniversalTime().ToString("o"),
            }.ToJsonString();

        /// <summary>
        /// Write the handshake file at <paramref name="path"/>, creating its directory, replacing any stale file
        /// from a crashed run, and restricting it to the owner where the platform allows. On Unix the mode is set at
        /// CREATE time rather than afterwards, so there is no window in which the token is world-readable. Windows
        /// has no equivalent one-call mode, so the file inherits the directory's ACL there, which is why the
        /// directory the options name should be the app data directory the game already owns.
        /// </summary>
        public static void Write(string path, int port, string token, int processId, DateTimeOffset startedAt)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var stream = OperatingSystem.IsWindows()
                ? new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
                : new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            using (stream)
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(Serialize(port, token, processId, startedAt));
        }

        /// <summary>Delete the handshake file, tolerating a file another process already removed.</summary>
        public static void Delete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>This process's id, the value the host stamps into the file.</summary>
        public static int CurrentProcessId => Environment.ProcessId;
    }
}
