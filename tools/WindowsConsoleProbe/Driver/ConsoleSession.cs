using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsConsoleValidation;

internal sealed class ConsoleSession : IDisposable
{
    private readonly SafeFileHandle input;
    private readonly SafeFileHandle output;
    internal string Window { get; }

    internal ConsoleSession()
    {
        // CI normally captures both streams. Use a private, genuine kernel console instead,
        // leaving the runner's console and log capture in its own process untouched.
        FreeConsole();
        NativeConsole.Require(AllocConsole(), "AllocConsole");
        Window = NativeConsole.GetConsoleWindow().ToInt64().ToString("x");
        ShowWindow(NativeConsole.GetConsoleWindow(), 0);
        var attributes = new SecurityAttributes { Size = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        output = CreateFileW("CONOUT$", 0xc0000000, 3, ref attributes, 3, 0, IntPtr.Zero);
        input = CreateFileW("CONIN$", 0xc0000000, 3, ref attributes, 3, 0, IntPtr.Zero);
        NativeConsole.Require(!output.IsInvalid && !input.IsInvalid, "Open private console handles");
        NativeConsole.Require(SetStdHandle(NativeConsole.StdInput, input.DangerousGetHandle()), "SetStdHandle input");
        NativeConsole.Require(SetStdHandle(NativeConsole.StdOutput, output.DangerousGetHandle()), "SetStdHandle output");
        NativeConsole.Require(SetStdHandle(NativeConsole.StdError, output.DangerousGetHandle()), "SetStdHandle error");
        if (!NativeConsole.ProcessIds().Contains((uint)Environment.ProcessId))
            throw new InvalidOperationException("Driver was not in its allocated console's process list.");
    }

    internal string Read() => NativeConsole.ReadBuffer(output);

    public void Dispose()
    {
        input.Dispose();
        output.Dispose();
        NativeConsole.Require(FreeConsole(), "FreeConsole");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Size;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, ref SecurityAttributes attributes,
        uint creation, uint flags, IntPtr template);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
