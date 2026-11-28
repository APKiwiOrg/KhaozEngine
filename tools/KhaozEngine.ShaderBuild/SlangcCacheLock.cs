using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace KhaozEngine.ShaderBuild;

/// <summary>The cross-process lock that serialises publishing into, and repairing, a slangc cache root.</summary>
internal static class SlangcCacheLock
{
    public const string FileName = ".publish.lock";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);

    // FileShare.None maps to an exclusive flock on Unix and a sharing lock on Windows, both released when the process dies.
    public static FileStream Acquire(string cacheRoot, TimeSpan timeout)
    {
        Directory.CreateDirectory(cacheRoot);
        var path = Path.Combine(cacheRoot, FileName);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < timeout)
            {
                Thread.Sleep(RetryInterval);
            }
            catch (IOException problem)
            {
                throw new ShaderBuildException(
                    ShaderBuildFailure.ToolchainMissing,
                    $"Another build held the slangc cache lock {path} for longer than {timeout}: {problem.Message} "
                    + $"Set {SlangcResolver.OverrideVariable} to a slangc executable to build without the cache.",
                    overrideVariable: SlangcResolver.OverrideVariable,
                    innerException: problem);
            }
        }
    }
}
