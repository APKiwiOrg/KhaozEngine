using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KhaozEngine.Platform;

// A snapshot is taken before attachment, which can replace the process standard-handle table.
internal readonly record struct WindowsConsoleHandle(int Id, IntPtr Handle, bool Redirected)
{
    internal const int Input = -10, Output = -11, Error = -12;

    internal static WindowsConsoleHandle Capture(int id)
    {
        IntPtr handle = GetStdHandle(id);
        uint fileType = GetFileType(handle);
        int error = Marshal.GetLastPInvokeError();
        bool consoleMode = GetConsoleMode(handle, out _);
        return new(id, handle, IsRedirected(handle, fileType, error, consoleMode));
    }

    internal static bool IsRedirected(IntPtr handle, uint fileType, int error, bool consoleMode)
        => handle != IntPtr.Zero && handle != new IntPtr(-1)
            && !(fileType == 0 && error == 6) // FILE_TYPE_UNKNOWN and ERROR_INVALID_HANDLE
            && !consoleMode;

    internal void RestoreRedirected()
    {
        if (Redirected && !SetStdHandle(Id, Handle))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal static void EnsureConsoleOutput(int id)
    {
        if (GetConsoleMode(GetStdHandle(id), out _)) return;

        // STARTF_USESTDHANDLES can leave an invalid handle in place even after attachment.
        using SafeFileHandle handle = CreateFileW("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !SetStdHandle(id, handle.DangerousGetHandle()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        // The standard-handle table owns this handle until process exit, even if Console.SetOut
        // later replaces the managed writer. OpenStandardOutput/Error wrap it without ownership.
        handle.SetHandleAsInvalid();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int id, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing,
        IntPtr attributes, uint creation, uint flags, IntPtr template);
}
