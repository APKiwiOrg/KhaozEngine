using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsConsoleValidation;

internal static class NativeConsole
{
    internal const int StdInput = -10, StdOutput = -11, StdError = -12;

    internal static uint StartupFlags()
    {
        GetStartupInfoW(out StartupInfo info);
        return info.Flags;
    }

    internal static void Require(bool success, string operation)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    internal static uint[] ProcessIds()
    {
        if (GetConsoleWindow() == IntPtr.Zero) return [];
        uint[] ids = new uint[64];
        uint count = GetConsoleProcessList(ids, (uint)ids.Length);
        Require(count > 0, "GetConsoleProcessList");
        if (count > ids.Length) throw new InvalidOperationException("Console process list exceeded finite probe capacity.");
        return ids[..(int)count];
    }

    internal static string ReadBuffer(SafeFileHandle output)
    {
        Require(GetConsoleScreenBufferInfo(output, out BufferInfo info), "GetConsoleScreenBufferInfo");
        uint cells = checked((uint)(info.Size.X * Math.Min(info.Size.Y, info.CursorPosition.Y + 2)));
        if (cells > 1_000_000) throw new InvalidOperationException("Console buffer exceeded finite probe capacity.");
        var text = new StringBuilder((int)cells);
        Require(ReadConsoleOutputCharacterW(output, text, cells, new Coord(0, 0), out uint read), "ReadConsoleOutputCharacterW");
        return text.ToString(0, (int)read);
    }

    internal static int ParentId()
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        Require(!snapshot.IsInvalid, "CreateToolhelp32Snapshot");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = string.Empty };
        Require(Process32FirstW(snapshot, ref entry), "Process32FirstW");
        do
        {
            if (entry.ProcessId == Environment.ProcessId) return checked((int)entry.ParentProcessId);
        } while (Process32NextW(snapshot, ref entry));
        throw new InvalidOperationException("Probe process missing from kernel process snapshot.");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Coord(short x, short y)
    {
        public readonly short X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public short Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferInfo
    {
        public Coord Size, CursorPosition;
        public ushort Attributes;
        public Rect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void GetStartupInfoW(out StartupInfo info);

    [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(SafeFileHandle output, out BufferInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacterW(SafeFileHandle output, StringBuilder text, uint cells, Coord start, out uint read);
}
