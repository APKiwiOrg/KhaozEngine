using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoveragePositionFailureIsolationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Only_the_parameterless_position_authority_can_write_actor_failure(int stage)
    {
        var kernel = CoverageCarryShaders.Kernels[stage];
        Validate(SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options), stage);
    }

    [Fact]
    public void A_native_direction_write_to_actor_failure_is_rejected_by_the_structural_guard()
    {
        var kernel = CoverageCarryShaders.Kernels[3];
        string mutant = kernel.Source.Replace("covPositionFailures();", """
            covPositionFailures();
            if (!covDirectionFinite3(covDirectionRead3(covRow(w,4u)+vertex*16u+3u)))
                atomicOr(CovResultWords[CovFrameWords[f+4u]+6u],4u);
            """, StringComparison.Ordinal);
        byte[] module = SpirvFrontEnd.ToSpirv(mutant, GpuShaderStages.Compute, "direction coupling negative control", CoverageCarryShaders.Options);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Validate(module, 3));
    }

    static void Validate(byte[] module, int stage)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var names = new Dictionary<uint, string>();
        var bodies = new Dictionary<uint, List<uint[]>>();
        var pointers = new HashSet<uint>();
        var workPointers = new HashSet<uint>();
        var constants = new Dictionary<uint, uint>();
        var definitions = new Dictionary<uint, uint[]>();
        var stores = new Dictionary<uint, uint>();
        uint function = 0, entry = 0;
        var writes = new List<uint>();
        for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
        {
            uint[] op = words.AsSpan(at, (int)(words[at] >> 16)).ToArray();
            uint code = op[0] & 65535;
            if (code == 5) names[op[1]] = Encoding.UTF8.GetString(MemoryMarshal.AsBytes(op.AsSpan(2))).TrimEnd('\0').Split('(')[0];
            if (code == 15) entry = op[2];
            if (code == 43 && op.Length == 4) constants[op[2]] = op[3];
            if (code == 71 && op[2] == 33 && op[3] == 3) pointers.Add(op[1]);
            if (code == 71 && op[2] == 33 && op[3] == 2) workPointers.Add(op[1]);
            if (code == 54) { function = op[2]; bodies[function] = []; }
            if (function != 0) bodies[function].Add(op);
            if (code is 65 or 66 && pointers.Contains(op[3])) pointers.Add(op[2]);
            if (code is 65 or 66 && workPointers.Contains(op[3])) workPointers.Add(op[2]);
            if (code == 62) stores[op[1]] = op[2];
            if (code is 57 or 61 or 65 or 66 or 83 or 124 or 128 or 132) definitions[op[2]] = op;
            if (code == 62 && pointers.Contains(op[1]) || code is >= 229 and <= 242 && pointers.Contains(op[3])) writes.Add(function);
            if (code == 228 && pointers.Contains(op[1])) writes.Add(function);
            if (code == 57) Assert.DoesNotContain(op[4..], p => pointers.Contains(p));
            if (code == 56) function = 0;
        }
        if (stage is 0 or 1 or 2) { Assert.Empty(writes); return; }
        if (stage == 4) { Assert.DoesNotContain(bodies.Values.SelectMany(b => b), op => (op[0] & 65535) == 241); return; }
        uint authority = Assert.Single(names, p => p.Value == "covPositionFailures").Key;
        Assert.NotEmpty(writes); Assert.All(writes, f => Assert.Equal(authority, f));
        Assert.DoesNotContain(bodies[authority], op => (op[0] & 65535) == 55);
        string[] allowed = ["covPositionFailures", "covFrame", "carryActor", "carryStride", "carryIndex", "covRow", "covTable", "covVertex", "covWork3", "covRead", "covF", "covFinite", "covFinite3", "covEqual", "covEqual3"];
        var visited = new HashSet<uint>();
        void Check(uint id)
        {
            if (!visited.Add(id)) return;
            Assert.Contains(names[id], allowed);
            Assert.DoesNotContain(bodies[id], op => (op[0] & 65535) is 12 or 129 or 131 or 133 or 136 or 142 or 148);
            foreach (var call in bodies[id].Where(op => (op[0] & 65535) == 57)) Check(call[3]);
        }
        Check(authority);
        var callers = bodies.SelectMany(b => b.Value.Where(op => (op[0] & 65535) == 57 && op[3] == authority).Select(op => b.Key)).ToArray();
        Assert.Equal(new[] { entry }, callers);
        int callAt = bodies[entry].FindIndex(op => (op[0] & 65535) == 57 && op[3] == authority);
        var prelude = bodies[entry].Take(callAt).ToArray();
        foreach (var call in prelude.Where(op => (op[0] & 65535) == 57))
            Assert.Contains(names[call[3]], new[] { "covFrame", "carryIndex" });
        Assert.DoesNotContain(prelude, op => (op[0] & 65535) == 61 && workPointers.Contains(op[3]));
        Assert.DoesNotContain(prelude, op => (op[0] & 65535) is 12 or 129 or 131 or 133 or 136 or 142 or 148 or >= 180 and <= 191);

        uint Unwrap(uint id)
        {
            for (int depth = 0; depth < 12; depth++)
            {
                if (stores.TryGetValue(id, out uint stored)) { id = stored; continue; }
                if (definitions.TryGetValue(id, out var op) && (op[0] & 65535) is 61 or 83) { id = op[3]; continue; }
                return id;
            }
            throw new InvalidOperationException("Unbounded position argument provenance.");
        }
        foreach (var call in bodies[authority].Where(op => (op[0] & 65535) == 57 && names[op[3]] == "covWork3"))
        {
            var address = definitions[Unwrap(call[4])];
            Assert.Equal(128u, address[0] & 65535);
            var row = definitions[Unwrap(address[3])];
            Assert.Equal(57u, row[0] & 65535); Assert.Equal("covRow", names[row[3]]);
            Assert.Contains(constants[Unwrap(row[5])], new uint[] { 0, 4, 8 });
            var stride = definitions[Unwrap(address[4])];
            Assert.Equal(132u, stride[0] & 65535); Assert.Equal(16u, constants[Unwrap(stride[4])]);
        }
        uint reader = Assert.Single(names, p => p.Value == "covWork3").Key;
        var offsets = bodies[reader].Where(op => (op[0] & 65535) == 128).Select(op => constants[op[4]]).ToArray();
        Assert.Equal(new uint[] { 1, 2 }, offsets);
    }
}
