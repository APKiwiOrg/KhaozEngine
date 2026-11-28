using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Core.Interop;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class NativeLibraryGateTests
{
    [NativeFact]
    public void Probe_reports_ready_for_the_built_library()
        => Assert.Equal(NativeLoadStatus.Ready, NativeLibraryGate.Probe());

    [NativeFact]
    public void Probe_reports_not_found_for_a_missing_library()
        => Assert.Equal(NativeLoadStatus.NotFound, NativeLibraryGate.Probe("khaoz_native_absent"));

    [NativeFact]
    public void Probe_reports_abi_mismatch()
        => Assert.Equal(NativeLoadStatus.AbiMismatch, NativeLibraryGate.Probe(expectedAbi: 999));

    [NativeFact]
    public void Probe_reports_abi_mismatch_for_the_previous_export_set()
        => Assert.Equal(NativeLoadStatus.AbiMismatch, NativeLibraryGate.Probe(expectedAbi: 2));

    [NativeFact]
    public async Task First_generated_import_refuses_a_stale_abi_library()
    {
        string libraryName = OperatingSystem.IsWindows() ? "khaoz_native.dll"
            : OperatingSystem.IsMacOS() ? "libkhaoz_native.dylib" : "libkhaoz_native.so";
        string fixtureName = libraryName.Replace("khaoz_native", "khaoz_native_stale_abi_fixture", StringComparison.Ordinal);
        string output = AppContext.BaseDirectory;
        string fixture = Path.Combine(output, fixtureName);
        Assert.True(File.Exists(fixture), $"The native ABI fixture is missing from the test output: {fixture}");

        DirectoryInfo childOutput = Directory.CreateTempSubdirectory("ke-native-abi-");
        try
        {
            foreach (string source in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(output, source);
                if (relative == libraryName || relative == fixtureName)
                    continue;

                string destination = Path.Combine(childOutput.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            File.Copy(fixture, Path.Combine(childOutput.FullName, libraryName));

            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = childOutput.FullName,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(Path.Combine(childOutput.FullName, typeof(NativeLibraryGateTests).Assembly.GetName().Name + ".dll"));
            start.ArgumentList.Add(NativeImportProbe.StaleAbiArgument);

            using Process child = Process.Start(start) ?? throw new InvalidOperationException("The native import probe did not start.");
            // Bound cleanup if the child hangs. The test result depends on its exit status.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            Task<string> readError = child.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await child.WaitForExitAsync(timeout.Token);
                Assert.True(child.ExitCode == 0, $"The native import probe exited {child.ExitCode}: {await readError}");
            }
            catch (OperationCanceledException)
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                throw;
            }
        }
        finally
        {
            childOutput.Delete(recursive: true);
        }
    }
}
