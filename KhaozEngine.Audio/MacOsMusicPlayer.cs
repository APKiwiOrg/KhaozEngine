using System;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>
/// Minimal AVAudioPlayer bridge for macOS DesktopGL music playback.
/// Avoids MonoGame's Song backend, which is currently unstable on macOS.
/// </summary>
internal sealed class MacOsMusicPlayer : IDisposable
{
    private static readonly IntPtr AutoreleasePoolClass;
    private static readonly IntPtr NSStringClass;
    private static readonly IntPtr NSURLClass;
    private static readonly IntPtr AVAudioPlayerClass;

    private static readonly IntPtr SelAlloc;
    private static readonly IntPtr SelDrain;
    private static readonly IntPtr SelFileUrlWithPath;
    private static readonly IntPtr SelInit;
    private static readonly IntPtr SelInitWithContentsOfUrlError;
    private static readonly IntPtr SelInitWithUtf8String;
    private static readonly IntPtr SelIsPlaying;
    private static readonly IntPtr SelLocalizedDescription;
    private static readonly IntPtr SelPlay;
    private static readonly IntPtr SelPrepareToPlay;
    private static readonly IntPtr SelRelease;
    private static readonly IntPtr SelSetNumberOfLoops;
    private static readonly IntPtr SelSetVolume;
    private static readonly IntPtr SelStop;
    private static readonly IntPtr SelUtf8String;

    private readonly ILogger _logger;
    private IntPtr _player;

    static MacOsMusicPlayer()
    {
        NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _);
        NativeLibrary.TryLoad("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", out _);

        AutoreleasePoolClass = objc_getClass("NSAutoreleasePool");
        NSStringClass = objc_getClass("NSString");
        NSURLClass = objc_getClass("NSURL");
        AVAudioPlayerClass = objc_getClass("AVAudioPlayer");

        SelAlloc = sel_registerName("alloc");
        SelDrain = sel_registerName("drain");
        SelFileUrlWithPath = sel_registerName("fileURLWithPath:");
        SelInit = sel_registerName("init");
        SelInitWithContentsOfUrlError = sel_registerName("initWithContentsOfURL:error:");
        SelInitWithUtf8String = sel_registerName("initWithUTF8String:");
        SelIsPlaying = sel_registerName("isPlaying");
        SelLocalizedDescription = sel_registerName("localizedDescription");
        SelPlay = sel_registerName("play");
        SelPrepareToPlay = sel_registerName("prepareToPlay");
        SelRelease = sel_registerName("release");
        SelSetNumberOfLoops = sel_registerName("setNumberOfLoops:");
        SelSetVolume = sel_registerName("setVolume:");
        SelStop = sel_registerName("stop");
        SelUtf8String = sel_registerName("UTF8String");
    }

    public MacOsMusicPlayer(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsPlaying
    {
        get
        {
            if (_player == IntPtr.Zero)
            {
                return false;
            }

            return SendByte(_player, SelIsPlaying) != 0;
        }
    }

    public bool Play(string path, float volume)
    {
        Stop();

        if (AVAudioPlayerClass == IntPtr.Zero)
        {
            _logger.Warn("Audio: AVAudioPlayer class not available");
            return false;
        }

        IntPtr pool = CreateAutoreleasePool();
        try
        {
            IntPtr nsPath = CreateNSString(path);
            try
            {
                IntPtr url = SendIntPtrIntPtr(NSURLClass, SelFileUrlWithPath, nsPath);
                IntPtr candidate = SendIntPtr(AVAudioPlayerClass, SelAlloc);
                candidate = SendIntPtrIntPtrOutIntPtr(candidate, SelInitWithContentsOfUrlError, url, out IntPtr error);

                if (candidate == IntPtr.Zero)
                {
                    _logger.Warn($"Audio: AVAudioPlayer init failed: {GetErrorDescription(error)}");
                    return false;
                }

                _player = candidate;
                SendVoidLong(_player, SelSetNumberOfLoops, 0);
                SendVoidFloat(_player, SelSetVolume, volume);
                SendByte(_player, SelPrepareToPlay);

                if (SendByte(_player, SelPlay) == 0)
                {
                    _logger.Warn("Audio: AVAudioPlayer play returned false");
                    Stop();
                    return false;
                }

                return true;
            }
            finally
            {
                Release(nsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Audio: AVAudioPlayer bridge failed: {ex.Message}");
            Stop();
            return false;
        }
        finally
        {
            DrainAutoreleasePool(pool);
        }
    }

    public void SetVolume(float volume)
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        SendVoidFloat(_player, SelSetVolume, volume);
    }

    public void Stop()
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SendVoid(_player, SelStop);
        }
        catch
        {
            // Best-effort cleanup.
        }

        Release(_player);
        _player = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
    }

    private static IntPtr CreateAutoreleasePool()
    {
        if (AutoreleasePoolClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr pool = SendIntPtr(AutoreleasePoolClass, SelAlloc);
        return SendIntPtr(pool, SelInit);
    }

    private static IntPtr CreateNSString(string value)
    {
        IntPtr nsString = SendIntPtr(NSStringClass, SelAlloc);
        return SendIntPtrString(nsString, SelInitWithUtf8String, value);
    }

    private static void DrainAutoreleasePool(IntPtr pool)
    {
        if (pool != IntPtr.Zero)
        {
            SendVoid(pool, SelDrain);
        }
    }

    private static string GetErrorDescription(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return "unknown error";
        }

        IntPtr description = SendIntPtr(error, SelLocalizedDescription);
        if (description == IntPtr.Zero)
        {
            return "unknown error";
        }

        IntPtr utf8 = SendIntPtr(description, SelUtf8String);
        return utf8 == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(utf8) ?? "unknown error";
    }

    private static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            SendVoid(handle, SelRelease);
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrIntPtrOutIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, out IntPtr arg2);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte SendByte(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidFloat(IntPtr receiver, IntPtr selector, float value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidLong(IntPtr receiver, IntPtr selector, long value);
}
