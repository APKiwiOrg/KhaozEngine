using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Validate the optimized production module's cache operands, rather than only GLSL or loop bounds.
internal static class WaterOpticsStreamedEmissionContract
{
    internal static void Verify(byte[] module)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var code = new List<uint[]>();
        for (int i = 5; i < words.Length;)
        {
            int size = checked((int)(words[i] >> 16));
            Assert.InRange(size, 1, words.Length - i);
            code.Add(words[i..(i + size)]);
            i += size;
        }
        static uint Op(uint[] instruction) => instruction[0] & 65535u;
        var constants = code.Where(i => Op(i) == 43 && i.Length == 4).ToDictionary(i => i[2], i => i[3]);
        uint uintType = Assert.Single(code, i => Op(i) == 21 && i[2] == 32 && i[3] == 0)[1];
        uint recordType = Assert.Single(code, i => Op(i) == 23 && i[2] == uintType && i[3] == 4)[1];
        uint[][] arrays = code.Where(i => Op(i) == 28).ToArray();
        Assert.DoesNotContain(arrays, i => constants[i[3]] == 18);
        uint bankType = Assert.Single(arrays, i => constants[i[3]] == 6 && i[2] == recordType)[1];
        Assert.Single(arrays, i => constants[i[3]] == 6);
        uint bankPointer = Assert.Single(code, i => Op(i) == 32 && i[2] == 7 && i[3] == bankType)[1];
        uint bank = Assert.Single(code, i => Op(i) == 59 && i[1] == bankPointer)[2];
        // The bank is never loaded, stored, copied, selected or passed whole. Only two element addresses exist.
        uint[][] uses = code.Where(i => Op(i) != 59 && i.Skip(1).Contains(bank)).ToArray();
        Assert.Equal(2, uses.Length);
        Assert.All(uses, i => { Assert.Equal(65u, Op(i)); Assert.Equal(5, i.Length); Assert.Equal(bank, i[3]); });
        uint[] write = Assert.Single(code, i => Op(i) == 62 && uses.Any(a => a[2] == i[1]));
        uint[] read = Assert.Single(code, i => Op(i) == 61 && uses.Any(a => a[2] == i[3]));
        Assert.Equal(recordType, read[1]);
        Assert.Contains(code, i => Op(i) == 80 && i[1] == recordType && i[2] == write[2]);
        Assert.Equal(uses[0][2], write[1]);
        Assert.Equal(uses[1][2], read[3]);
        Assert.All(uses, address => Assert.Contains(code, i => Op(i) == 177 && i[3] == address[4]
            && constants.GetValueOrDefault(i[4]) == 6));
        uint[][] extracts = code.Where(i => Op(i) == 81 && i[3] == read[2]).ToArray();
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, extracts.Select(i => i[4]).Order().ToArray());
        Assert.All(extracts, i => Assert.Equal(5, i.Length));
        Assert.DoesNotContain(code, i => Op(i) == 77 && i[3] == read[2]); // No dynamic record component.
        // Trace the two scalar normalized additions into unsigned overflow tests and scalar counters.
        // Stop at this cache contribution loop's merge, before later unsigned loop comparisons.
        uint[] visitBound = Assert.Single(code, i => Op(i) == 177 && i[3] == uses[1][4]
            && constants.GetValueOrDefault(i[4]) == 6);
        uint[] visitLoop = code.Skip(code.IndexOf(visitBound)).First(i => Op(i) == 246);
        int readAt = code.IndexOf(read);
        int visitEnd = code.FindIndex(readAt, i => Op(i) == 248 && i[1] == visitLoop[1]);
        Assert.True(code.IndexOf(visitLoop) < readAt && visitEnd > readAt);
        uint[][] wraps = code.Skip(readAt).Take(visitEnd - readAt).Where(i => Op(i) == 176).ToArray();
        Assert.Equal(2, wraps.Length);
        foreach (uint[] wrap in wraps)
        {
            uint[] addition = Assert.Single(code, i => Op(i) == 128 && i[2] == wrap[3]);
            Assert.Equal(uintType, addition[1]);
            Assert.Equal(addition[3], wrap[4]);
            uint[] bit = Assert.Single(code, i => Op(i) == 169 && i[3] == wrap[2]);
            Assert.Equal(1u, constants[bit[4]]);
            Assert.Equal(0u, constants[bit[5]]);
            Assert.Contains(code, i => Op(i) == 128 && i[1] == uintType && i[4] == bit[2]);
        }
    }
}
