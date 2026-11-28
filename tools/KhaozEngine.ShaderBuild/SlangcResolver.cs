using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace KhaozEngine.ShaderBuild;

/// <summary>Finds the pinned slangc: a version-checked override, else a digest-verified download cached per version and host.</summary>
public static class SlangcResolver
{
    public const string OverrideVariable = "SLANGC";
    public const string CacheVariable = "KHAOZ_SLANGC_CACHE";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>KHAOZ_SLANGC_CACHE when set, else LocalApplicationData/khaozengine/slangc.</summary>
    public static string DefaultCacheRoot(Func<string, string?>? environment = null)
    {
        var configured = (environment ?? Environment.GetEnvironmentVariable)(CacheVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "khaozengine", "slangc")
            : configured;
    }

    /// <summary>Returns the path of a slangc that reports the pinned version, downloading it into the cache when needed.</summary>
    public static string Resolve(
        SlangcPins pins,
        string? slangcOverride,
        string cacheRoot,
        HttpMessageHandler? handler = null,
        Func<string, string>? versionProbe = null)
        => Resolve(pins, slangcOverride, cacheRoot, handler, versionProbe, HostRid());

    internal static string Resolve(
        SlangcPins pins,
        string? slangcOverride,
        string cacheRoot,
        HttpMessageHandler? handler,
        Func<string, string>? versionProbe,
        string? hostRid)
    {
        var probe = versionProbe ?? ProbeVersion;
        if (!string.IsNullOrEmpty(slangcOverride))
        {
            RequireVersion(pins, slangcOverride, probe);
            return slangcOverride;
        }

        if (hostRid is null || !pins.Assets.TryGetValue(hostRid, out var asset))
            throw new ShaderBuildException(
                ShaderBuildFailure.ToolchainMissing,
                $"slangc {pins.Version} has no pinned download for this host ({hostRid ?? RuntimeInformation.RuntimeIdentifier}). "
                + $"Set {OverrideVariable} to a slangc {pins.Version} executable.",
                overrideVariable: OverrideVariable);

        var folder = Path.Combine(cacheRoot, pins.Version, hostRid);
        var executable = Path.Combine(folder, asset.Executable);
        if (File.Exists(executable))
            return executable;
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);

        Install(pins, hostRid, asset, cacheRoot, folder, handler, probe);
        return executable;
    }

    private static void Install(
        SlangcPins pins, string rid, SlangcAsset asset, string cacheRoot, string folder, HttpMessageHandler? handler, Func<string, string> probe)
    {
        var staging = Path.Combine(cacheRoot, $".staging-{Guid.NewGuid():N}");
        var archive = Path.Combine(staging, "slang.tar.gz");
        var extracted = Path.Combine(staging, rid);
        try
        {
            Directory.CreateDirectory(extracted);
            Download(pins, asset, handler, archive);
            RequireDigest(asset, archive);
            Unpack(pins, asset, archive, extracted);

            var stagedExecutable = Path.Combine(extracted, asset.Executable);
            if (!File.Exists(stagedExecutable))
                throw new ShaderBuildException(
                    ShaderBuildFailure.ToolchainMissing,
                    $"The slangc {pins.Version} archive {asset.Url} holds no {asset.Executable}. "
                    + $"Set {OverrideVariable} to a slangc {pins.Version} executable.",
                    asset.Url,
                    OverrideVariable);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    stagedExecutable,
                    File.GetUnixFileMode(stagedExecutable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            RequireVersion(pins, stagedExecutable, probe);
            Publish(extracted, folder, asset.Executable);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void Download(SlangcPins pins, SlangcAsset asset, HttpMessageHandler? handler, string archive)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = DownloadTimeout;
        try
        {
            using var response = client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var body = response.Content.ReadAsStream();
            using var file = File.Create(archive);
            body.CopyTo(file);
        }
        catch (Exception problem) when (problem is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ShaderBuildException(
                ShaderBuildFailure.DownloadFailed,
                $"Could not download slangc {pins.Version} from {asset.Url}: {problem.Message} "
                + $"Set {OverrideVariable} to a slangc {pins.Version} executable to build without the download.",
                asset.Url,
                OverrideVariable,
                problem);
        }
    }

    private static void Unpack(SlangcPins pins, SlangcAsset asset, string archive, string extracted)
    {
        try
        {
            using var compressed = File.OpenRead(archive);
            TarGzExtractor.Extract(compressed, extracted);
        }
        catch (Exception problem) when (problem is InvalidDataException or EndOfStreamException)
        {
            throw new ShaderBuildException(
                ShaderBuildFailure.ToolchainMissing,
                $"The slangc {pins.Version} archive {asset.Url} could not be unpacked: {problem.Message} "
                + $"Set {OverrideVariable} to a slangc {pins.Version} executable.",
                asset.Url,
                OverrideVariable,
                problem);
        }
    }

    private static void RequireDigest(SlangcAsset asset, string archive)
    {
        string actual;
        using (var file = File.OpenRead(archive))
            actual = Convert.ToHexStringLower(SHA256.HashData(file));
        if (actual != asset.Sha256)
            throw new ShaderBuildException(
                ShaderBuildFailure.DigestMismatch,
                $"The download from {asset.Url} has SHA-256 {actual}, but the pin requires {asset.Sha256}.",
                asset.Url);
    }

    // Directory.Move is a rename on one volume, so a reader only ever sees a missing folder or a complete one.
    private static void Publish(string extracted, string folder, string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        try
        {
            Directory.Move(extracted, folder);
        }
        catch (Exception lost) when ((lost is IOException or UnauthorizedAccessException) && File.Exists(Path.Combine(folder, executable)))
        {
        }
    }

    private static void RequireVersion(SlangcPins pins, string slangc, Func<string, string> probe)
    {
        var reported = probe(slangc).Trim();
        if (reported != pins.Version)
            throw new ShaderBuildException(
                ShaderBuildFailure.ToolchainVersion,
                $"slangc at {slangc} reports version '{reported}', but the pin requires {pins.Version}. "
                + $"Point {OverrideVariable} at slangc {pins.Version}, or unset it to use the pinned download.",
                overrideVariable: OverrideVariable);
    }

    private static string ProbeVersion(string slangc)
    {
        var start = new ProcessStartInfo(slangc)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-version");
        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            var errorText = error.GetAwaiter().GetResult();
            process.WaitForExit();
            return string.IsNullOrWhiteSpace(output) ? errorText : output;
        }
        catch (Win32Exception problem)
        {
            throw new ShaderBuildException(
                ShaderBuildFailure.ToolchainMissing,
                $"Could not run slangc at {slangc}: {problem.Message}",
                overrideVariable: OverrideVariable,
                innerException: problem);
        }
    }

    private static string? HostRid() => (RuntimeInformation.OSArchitecture, OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), OperatingSystem.IsWindows()) switch
    {
        (Architecture.Arm64, true, _, _) => "osx-arm64",
        (Architecture.X64, _, true, _) => "linux-x64",
        (Architecture.Arm64, _, true, _) => "linux-arm64",
        (Architecture.X64, _, _, true) => "win-x64",
        _ => null,
    };
}
