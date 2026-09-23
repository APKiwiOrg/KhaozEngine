using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace KhaozEngine.Tests;

/// <summary>
/// Asks a .NET process's own diagnostic server to write a dump of that process, which is what
/// <c>dotnet-dump collect</c> does, spoken directly so no diagnostics package is needed.
///
/// <para>This exists because the obvious route does not work on Windows. Launching <c>createdump</c> from next to
/// <c>System.Private.CoreLib.dll</c> with a pid is refused there ("The pid argument is no longer supported"),
/// which is what the first armed Windows run of the watchdog reported. The runtime's own dump command is the
/// supported way in, and the runtime then launches that same <c>createdump</c> itself. The diagnostic server
/// runs on a native runtime thread, so it answers even while the managed thread pool is starved.</para>
///
/// <para>The wire format is the diagnostics IPC protocol: a 20 byte header (the 14 byte magic
/// <c>DOTNET_IPC_V1</c> with its terminator, a little endian uint16 total size, the command set, the command id,
/// two reserved bytes), then the payload. The dump command is set 0x01, id 0x01, carrying the dump path as a
/// length prefixed UTF16 string with its terminator, the dump type and a diagnostics flag. The reply is set 0xFF
/// with id 0x00 for success or 0xFF for an error, carrying an HRESULT.</para>
/// </summary>
internal static class DiagnosticsIpcDump
{
    private const byte DumpCommandSet = 0x01;
    private const byte GenerateCoreDump = 0x01;
    private const byte ServerCommandSet = 0xFF;
    private const byte ServerOk = 0x00;
    private const uint WithHeap = 2;
    private const int HeaderSize = 20;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DOTNET_IPC_V1\0");

    /// <summary>Requests a dump with the heap of process <paramref name="pid"/> at <paramref name="path"/>,
    /// blocking until the runtime answers. Returns the reply's HRESULT, 0 on success.</summary>
    /// <exception cref="IOException">The diagnostic server could not be reached or answered malformed.</exception>
    public static int WriteDump(int pid, string path, TimeSpan connectBudget)
    {
        using Stream stream = Connect(pid, connectBudget);
        stream.Write(Request(path));
        stream.Flush();

        byte[] header = new byte[HeaderSize];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new IOException("the diagnostic server answered without the IPC magic");
        int size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        byte[] payload = new byte[Math.Max(0, size - HeaderSize)];
        stream.ReadExactly(payload);

        int hresult = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : 0;
        bool ok = header[16] == ServerCommandSet && header[17] == ServerOk;
        return ok ? hresult : (hresult != 0 ? hresult : unchecked((int)0x80004005));
    }

    private static byte[] Request(string path)
    {
        byte[] name = Encoding.Unicode.GetBytes(path + "\0");
        int size = HeaderSize + 4 + name.Length + 4 + 4;
        byte[] packet = new byte[size];
        Span<byte> span = packet;
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], checked((ushort)size));
        span[16] = DumpCommandSet;
        span[17] = GenerateCoreDump;
        int at = HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)(path.Length + 1));
        at += 4;
        name.CopyTo(span[at..]);
        at += name.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], WithHeap);
        at += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], 0);
        return packet;
    }

    private static Stream Connect(int pid, TimeSpan budget)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", $"dotnet-diagnostic-{pid}", PipeDirection.InOut);
            pipe.Connect((int)budget.TotalMilliseconds);
            return pipe;
        }

        // The runtime names its socket dotnet-diagnostic-{pid}-{disambiguation key}-socket in the temp directory.
        string socketPath = Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{pid}-*-socket")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? throw new IOException($"no diagnostic socket for pid {pid} in {Path.GetTempPath()}");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
