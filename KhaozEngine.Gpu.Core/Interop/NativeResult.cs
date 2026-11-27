using System;
using System.Text;

namespace KhaozEngine.Gpu.Core.Interop;

internal static class NativeResult
{
    internal static void ThrowIfFailed(khaoz_result result, string operation)
    {
        if (result == khaoz_result.KHAOZ_OK)
            return;

        string diagnostic = ReadLastError();
        string message = diagnostic.Length == 0
            ? $"{operation} failed ({result})."
            : $"{operation} failed ({result}). {diagnostic}";

        throw result switch
        {
            khaoz_result.KHAOZ_ERROR_INVALID_ARGUMENT => new ArgumentException(message),
            khaoz_result.KHAOZ_ERROR_UNSUPPORTED => new NotSupportedException(message),
            _ => new InvalidOperationException(message),
        };
    }

    private static unsafe string ReadLastError()
    {
        // The diagnostic belongs to the calling thread. Query the length and copy it on that thread.
        int length = checked((int)KhaozNative.khaoz_last_error_message(null, 0));
        if (length == 0)
            return string.Empty;

        byte[] utf8 = new byte[checked(length + 1)];
        fixed (byte* buffer = utf8)
            KhaozNative.khaoz_last_error_message((sbyte*)buffer, (nuint)utf8.Length);

        return Encoding.UTF8.GetString(utf8, 0, length);
    }
}
