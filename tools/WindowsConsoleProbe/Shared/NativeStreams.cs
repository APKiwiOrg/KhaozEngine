using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsConsoleValidation;

internal static class NativeStreams
{
    internal static SafeFileHandle Open(string path, bool input = false)
    {
        SafeFileHandle handle = CreateFileW(path, input ? 0x80000000u : 0x40000000u,
            3, IntPtr.Zero, input ? 3u : 2u, 0, IntPtr.Zero);
        NativeConsole.Require(!handle.IsInvalid, "CreateFileW " + path);
        return handle;
    }

    internal static WriteResult Write(int which, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\r\n");
        bool success = WriteFile(NativeConsole.GetStdHandle(which), bytes, (uint)bytes.Length, out uint written, IntPtr.Zero);
        return new(success && written == bytes.Length, success ? 0 : Marshal.GetLastWin32Error(), written);
    }

    internal static string ReadInput()
    {
        byte[] bytes = new byte[256];
        NativeConsole.Require(ReadFile(NativeConsole.GetStdHandle(NativeConsole.StdInput), bytes,
            (uint)bytes.Length, out uint read, IntPtr.Zero), "ReadFile stdin");
        return Encoding.UTF8.GetString(bytes, 0, checked((int)read));
    }

    internal static (SafeFileHandle Read, SafeFileHandle Write) Pipe()
    {
        var attributes = new SecurityAttributes { Size = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        NativeConsole.Require(CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref attributes, 4096), "CreatePipe");
        try
        {
            NativeConsole.Require(SetHandleInformation(read, 1, 0), "Disable pipe read inheritance");
            return (read, write);
        }
        catch { read.Dispose(); write.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Size;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr attributes,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr handle, byte[] bytes, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr handle, [Out] byte[] bytes, uint count, out uint read, IntPtr overlapped);
}
