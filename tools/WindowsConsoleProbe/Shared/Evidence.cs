using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowsConsoleValidation;

internal sealed record StreamState(long Handle, uint FileType, int FileTypeError, bool ConsoleMode)
{
    internal static StreamState Capture(int which)
    {
        IntPtr handle = NativeConsole.GetStdHandle(which);
        uint type = NativeConsole.GetFileType(handle);
        int error = Marshal.GetLastWin32Error();
        return new(handle.ToInt64(), type, error, NativeConsole.GetConsoleMode(handle, out _));
    }
}

internal sealed record ConsoleState(bool HasConsole, string Window, bool OutputRedirected, bool ErrorRedirected,
    StreamState Input, StreamState Output, StreamState Error, uint[] ProcessIds)
{
    internal static ConsoleState Capture() => new(
        NativeConsole.GetConsoleWindow() != IntPtr.Zero,
        NativeConsole.GetConsoleWindow().ToInt64().ToString("x"),
        Console.IsOutputRedirected, Console.IsErrorRedirected,
        StreamState.Capture(NativeConsole.StdInput), StreamState.Capture(NativeConsole.StdOutput),
        StreamState.Capture(NativeConsole.StdError), NativeConsole.ProcessIds());
}

internal sealed record WriteResult(bool Success, int Error, uint Bytes);
internal sealed record ProbeEvidence(string Marker, string Mode, int ProcessId, int ParentProcessId,
    string EngineVersion, uint StartupFlags, ConsoleState Entry, ConsoleState Before, ConsoleState After,
    ConsoleState AfterSecond, bool FirstAttached, bool SecondAttached, bool EngineHasConsole,
    WriteResult? NativeOutput, WriteResult? NativeError, string? InputText, string? Failure);
internal sealed record CheckResult(string Name, string Status, string Detail);

internal static class Evidence
{
    internal static string EngineVersion => Assembly.GetEntryAssembly()!.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "WindowsConsoleProbe.EngineVersion").Value!;

    internal static void Save<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))
        ?? throw new InvalidOperationException("Empty evidence file.");

    internal static string Managed(string marker, string stream) => "KE-WINDOWS-MANAGED-" + stream + "-" + marker;
    internal static string Native(string marker, string stream) => "KE-WINDOWS-NATIVE-" + stream + "-" + marker;
    internal static string Expected(string marker, string stream) => Managed(marker, stream) + "\r\n" + Native(marker, stream) + "\r\n";
    internal static string Input(string marker) => "KE-WINDOWS-STDIN-" + marker;
}
