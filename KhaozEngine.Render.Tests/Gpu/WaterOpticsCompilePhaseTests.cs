using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One cacheless production Water pair, without device or pipeline creation. Manual CI only.</summary>
[Collection("GpuTimingSensitive")]
public sealed class WaterOpticsCompilePhaseTests(ITestOutputHelper output)
{
    const string HarnessPath = "KhaozEngine.Render.Tests/Gpu/WaterOpticsCompilePhaseTests.cs";
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [WaterCompileProofFact]
    public void ProductionWaterPair_RecordsCompilerPhases()
    {
        // A required run must fail off Windows, never pass through an early return or a platform skip.
        Assert.True(OperatingSystem.IsWindows(), "The required Water compile proof needs Windows FXC.");
        if (OperatingSystem.IsWindows()) RunWindows();
    }

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    void RunWindows()
    {
        Assert.Equal("1", Required("KE_WATER_COMPILE_PROOF"));
        Assert.Equal("off", Required("KE_SPIRV_CACHE"));
        Assert.Equal("off", Required("KE_D3D11_SHADER_CACHE"));
        Assert.Equal("off", Required("KE_D3D11_DEBUG"));
        uint flags = D3D11ShaderDebug.FromEnvironment(out string? unknown);
        Assert.Null(unknown);
        Assert.Equal(D3D11ShaderDebug.Optimized, flags);
        Assert.Equal("vs_5_0", D3D11ShaderProfile.Vertex);
        Assert.Equal("ps_5_0", D3D11ShaderProfile.Fragment);

        string root = Required("KE_WATER_COMPILE_SOURCE_ROOT");
        string variant = Required("KE_WATER_COMPILE_VARIANT");
        Assert.Equal("candidate", variant);
        string programChoice = Required("KE_WATER_COMPILE_PROGRAM");
        Assert.Contains(programChoice, new[] { "default", "triangle" });
        bool triangle = programChoice == "triangle";
        string program = triangle ? "WaterTriangle" : "Water";
        string fragmentIdentity = triangle ? "ShaderSources.WaterTriangleFrag" : "ShaderSources.WaterFrag";
        string artifactPrefix = "water-" + programChoice;
        string harnessSha = Required("KE_WATER_COMPILE_HARNESS_SHA");
        Assert.Matches("^[0-9a-f]{40}$", harnessSha);
        string expectedSource = harnessSha;
        string sourceSha = GitHead(root);
        Assert.Equal(expectedSource, Required("KE_WATER_COMPILE_SOURCE_SHA"));
        Assert.Equal(expectedSource, sourceSha);
        string harnessHash = Hash(File.ReadAllBytes(Path.Combine(root, HarnessPath)));
        Assert.Equal(Required("KE_WATER_COMPILE_HARNESS_SHA256"), harnessHash);
        string directory = Path.GetFullPath(Required("KE_WATER_COMPILE_OUTPUT"));
        Assert.False(Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(),
            "Each invocation needs a fresh artifact directory.");
        Directory.CreateDirectory(directory);

        SpirvCompileCache cache = SpirvCompileCache.Shared;
        Assert.False(cache.Enabled);
        var before = Counters(cache);
        Assert.Equal(0L, cache.CompileCount);
        Assert.Equal(0L, cache.HitCount);
        Assert.Equal(0, cache.Count);
        using Process process = Process.GetCurrentProcess();
        string[] nativeBefore = NativePaths(process);
        var phases = new List<Phase>();
        var artifacts = new Dictionary<string, Artifact>();
        bool verified = false;
        string? failure = null;
        try
        {
            string vertex = ShaderSources.WaterVert;
            string fragment = triangle ? ShaderSources.WaterTriangleFrag : ShaderSources.WaterFrag;
            Save(artifactPrefix + ".vert.glsl", Encoding.UTF8.GetBytes(vertex));
            Save(artifactPrefix + ".frag.glsl", Encoding.UTF8.GetBytes(fragment));

            byte[] vertexSpirv = Measure("vertexFrontEnd", () => FrontEnd(vertex, GpuShaderStages.Vertex));
            Assert.Equal(1L, cache.CompileCount);
            Save(artifactPrefix + ".vert.spv", vertexSpirv);
            byte[] fragmentSpirv = Measure("fragmentFrontEnd", () => FrontEnd(fragment, GpuShaderStages.Fragment));
            Assert.Equal(2L, cache.CompileCount);
            Save(artifactPrefix + ".frag.spv", fragmentSpirv);
            CrossCompiledPair pair = Measure("pairHlsl", () => Hlsl(vertexSpirv, fragmentSpirv));
            Save(artifactPrefix + ".vert.hlsl", Encoding.UTF8.GetBytes(pair.VertexSource));
            Save(artifactPrefix + ".frag.hlsl", Encoding.UTF8.GetBytes(pair.FragmentSource));
            byte[] vertexDxbc = Measure("vertexFxc", () => Fxc(pair.VertexSource, D3D11ShaderProfile.Vertex, flags));
            VerifyDxbc(vertexDxbc, 1); // D3D11_SHVER_VERTEX_SHADER, shader model 5.0.
            Save(artifactPrefix + ".vert.dxbc", vertexDxbc);
            byte[] fragmentDxbc = Measure("pixelFxc", () => Fxc(pair.FragmentSource, D3D11ShaderProfile.Fragment, flags));
            VerifyDxbc(fragmentDxbc, 0); // D3D11_SHVER_PIXEL_SHADER, shader model 5.0.
            Save(artifactPrefix + ".frag.dxbc", fragmentDxbc);
            var inputs = Measure("vertexReflection", () => D3D11Fxc.ReflectVertexInputs(vertexDxbc, "Water"));
            Assert.NotEmpty(inputs);
            Assert.Equal(2L, cache.CompileCount);
            Assert.Equal(0L, cache.HitCount);
            Assert.Equal(0, cache.Count);
            Assert.Equal(6, phases.Count(p => p.Succeeded));
            Assert.Contains(NativePaths(process), p => Path.GetFileName(p).Contains("d3dcompiler", StringComparison.OrdinalIgnoreCase));
            verified = true;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            var result = new
            {
                schema = "water-compile-phases-v1",
                variant,
                sourceSha,
                expectedSourceSha = expectedSource,
                harnessSha,
                harnessSha256 = harnessHash,
                verified,
                failure,
                program,
                programChoice,
                vertexSource = "ShaderSources.WaterVert",
                fragmentSource = fragmentIdentity,
                artifactPrefix,
                vertexProfile = D3D11ShaderProfile.Vertex,
                pixelProfile = D3D11ShaderProfile.Fragment,
                entryPoint = D3D11ShaderProfile.EntryPoint,
                flags,
                flagName = "OptimizationLevel3",
                frontEndPin = SpirvFrontEndPin.Identity,
                hlslPin = HlslCrossCompilePin.Identity,
                toolchain = SpirvToolchainVersion.Identity,
                environment = new
                {
                    os = RuntimeInformation.OSDescription,
                    framework = RuntimeInformation.FrameworkDescription,
                    runtime = RuntimeInformation.RuntimeIdentifier,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    sdk = Required("KE_WATER_COMPILE_SDK"),
                    processors = Environment.ProcessorCount,
                    image = Environment.GetEnvironmentVariable("ImageOS"),
                    imageVersion = Environment.GetEnvironmentVariable("ImageVersion"),
                    spirvCache = "off",
                    dxbcCache = "off",
                    d3d11Debug = "off",
                },
                cacheBefore = before,
                cacheAfter = Counters(cache),
                dxbcCacheAccess = "bypassed by direct production compiler seam, no disk cache calls",
                nativeModulesBefore = nativeBefore,
                nativeModulesAfter = NativePaths(process).Select(p => new
                {
                    path = p,
                    version = FileVersionInfo.GetVersionInfo(p).FileVersion,
                    sha256 = Hash(File.ReadAllBytes(p)),
                }).ToArray(),
                phaseCount = phases.Count,
                successfulPhaseCount = phases.Count(p => p.Succeeded),
                phases,
                artifacts,
                timingScope = "One call per phase. First native loading and JIT remain in the first phase needing them. "
                    + "CPU is process-wide and includes testhost activity. FXC spans include wrapper and byte extraction. "
                    + "No prewarm, device, Scene3D, pipeline, clipmap, frames or full shader catalog.",
            };
            string json = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(Path.Combine(directory, "measurements.json"), json + Environment.NewLine);
            output.WriteLine(json);
        }

        T Measure<T>(string name, Func<T> action)
        {
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime;
            long start = Stopwatch.GetTimestamp();
            bool succeeded = false;
            try
            {
                T value = action();
                succeeded = true;
                return value;
            }
            finally
            {
                double wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                process.Refresh();
                double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var phase = new Phase(name, wallMs, cpuMs, succeeded, cache.CompileCount, cache.HitCount);
                phases.Add(phase);
                output.WriteLine(JsonSerializer.Serialize(phase, JsonOptions));
            }
        }

        void Save(string name, byte[] bytes)
        {
            Assert.NotEmpty(bytes);
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
            string hash = Hash(bytes);
            Assert.Equal(hash, Hash(File.ReadAllBytes(Path.Combine(directory, name))));
            artifacts.Add(name, new Artifact(bytes.Length, hash));
        }
    }

    // Keep first-use native initialization inside Measure, even when the caller is JIT-compiled early.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static byte[] FrontEnd(string source, GpuShaderStages stage) => SpirvFrontEnd.ToSpirv(source, stage, "Water");

    [MethodImpl(MethodImplOptions.NoInlining)]
    static CrossCompiledPair Hlsl(byte[] vertex, byte[] fragment) => SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, "Water");

    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static byte[] Fxc(string source, string profile, uint flags) => D3D11Fxc.Compile(source, profile, flags, "Water");

    // Inspect the returned container and executable version token, independent of the requested profile.
    static void VerifyDxbc(byte[] bytes, uint stage)
    {
        Assert.True(bytes.Length >= 36, "FXC must produce a nonempty DXBC container.");
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("DXBC"u8));
        Assert.Equal((uint)bytes.Length, Word(24));
        uint count = Word(28);
        Assert.InRange(count, 1u, (uint)(bytes.Length - 32) / 4);
        int programs = 0;
        for (int i = 0; i < count; i++)
        {
            uint offset = Word(32 + 4 * i);
            Assert.InRange(offset, 32u + 4 * count, (uint)bytes.Length - 8);
            int chunk = checked((int)offset);
            uint size = Word(chunk + 4);
            Assert.InRange(size, 0u, (uint)bytes.Length - offset - 8);
            ReadOnlySpan<byte> tag = bytes.AsSpan(chunk, 4);
            if (!tag.SequenceEqual("SHDR"u8) && !tag.SequenceEqual("SHEX"u8)) continue;
            Assert.True(size >= 8);
            uint version = Word(chunk + 8);
            Assert.Equal(stage, version >> 16);
            Assert.Equal(0x50u, version & 0xffffu);
            Assert.Equal(size / 4, Word(chunk + 12));
            programs++;
        }
        Assert.Equal(1, programs);
        uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), "Missing required proof setting " + name);
        return value!;
    }

    static string GitHead(string root)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("HEAD");
        using Process git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        string sha = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return sha;
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static object Counters(SpirvCompileCache cache) => new { cache.Enabled, cache.CompileCount, cache.HitCount, cache.Count };
    static string[] NativePaths(Process process)
    {
        process.Refresh();
        return process.Modules.Cast<ProcessModule>()
            .Where(m => m.ModuleName.Contains("shaderc", StringComparison.OrdinalIgnoreCase)
                || m.ModuleName.Contains("spirv", StringComparison.OrdinalIgnoreCase)
                || m.ModuleName.Contains("d3dcompiler", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.FileName).Order(StringComparer.Ordinal).ToArray();
    }

    sealed record Phase(string Name, double WallMs, double ProcessCpuMs, bool Succeeded, long FrontEndCompileCount, long FrontEndHitCount);
    sealed record Artifact(int Bytes, string Sha256);
}

/// <summary>Ordinary discovery skips the proof. A required run is never silently skipped, even off Windows.</summary>
public sealed class WaterCompileProofFactAttribute : FactAttribute
{
    public WaterCompileProofFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KE_WATER_COMPILE_PROOF_REQUIRED") == "1") return;
        if (!OperatingSystem.IsWindows()) Skip = "Water compile phase proof requires Windows FXC.";
        else if (Environment.GetEnvironmentVariable("KE_WATER_COMPILE_PROOF") != "1")
            Skip = "Manual-only proof. Set KE_WATER_COMPILE_PROOF=1 with the proof metadata and cache controls.";
    }
}
