using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// FXC can implement dynamic vector indexing arithmetically, letting an unselected infinity poison the result.
internal static class CoverageSpirvAccessAudit
{
    internal static void Validate(byte[] spirv, string label, bool constantPrivateArrays = false)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
        var instructions = new List<uint[]>();
        var types = new Dictionary<uint, uint[]>();
        var constants = new Dictionary<uint, uint>();
        var valueTypes = new Dictionary<uint, uint>();
        for (int offset = 5; offset < words.Length;)
        {
            int length = (int)(words[offset] >> 16);
            Assert.True(length > 0 && offset + length <= words.Length, $"{label}: malformed SPIR-V instruction");
            uint[] instruction = words.AsSpan(offset, length).ToArray();
            uint opcode = instruction[0] & 0xffff;
            Assert.True(opcode is not (77 or 78), $"{label}: C5 prohibits {(opcode == 77 ? "OpVectorExtractDynamic" : "OpVectorInsertDynamic")}");
            if (opcode is >= 19 and <= 33) types.Add(instruction[1], instruction);
            instructions.Add(instruction);
            offset += length;
        }
        foreach (uint[] instruction in instructions)
        {
            uint opcode = instruction[0] & 0xffff;
            if (opcode is 43 or 46 && (types[instruction[1]][0] & 0xffff) == 21)
                constants.Add(instruction[2], opcode == 46 ? 0u : instruction[3]);
            // Track pointer-producing values, including parameters, aliases, selections and chained results.
            if (opcode is 45 or 55 or 57 or 59 or 60 or 61 or 65 or 66 or 67 or 70 or 83 or 120 or 124 or 169 or 245
                && types.TryGetValue(instruction[1], out uint[]? type) && (type[0] & 0xffff) == 32)
                valueTypes.Add(instruction[2], instruction[1]);
        }
        foreach (uint[] instruction in instructions)
        {
            uint opcode = instruction[0] & 0xffff;
            if (opcode is not (65 or 66)) continue;
            uint[] pointer = types[valueTypes[instruction[3]]];
            if (pointer[2] is not (6 or 7)) continue; // Private or Function storage
            uint typeId = pointer[3];
            for (int index = 4; index < instruction.Length; index++)
            {
                uint[] type = types[typeId];
                uint typeOpcode = type[0] & 0xffff;
                bool constant = constants.TryGetValue(instruction[index], out uint member);
                Assert.True(typeOpcode is not (23 or 24) || constant,
                    $"{label}: C5 prohibits non-constant {(opcode == 65 ? "OpAccessChain" : "OpInBoundsAccessChain")} "
                    + $"index %{instruction[index]} into {(typeOpcode == 23 ? "vector" : "matrix")} type %{typeId}");
                Assert.True(!constantPrivateArrays || typeOpcode is not (28 or 29) || constant,
                    $"{label}: C6 prohibits non-constant private array index %{instruction[index]}");
                // C6 exact-word helpers also need constant array accesses for FXC addressability.
                if (typeOpcode == 30)
                {
                    Assert.True(constant && member < type.Length - 2, $"{label}: invalid structure member index");
                    typeId = type[2 + (int)member];
                }
                else
                {
                    Assert.True(typeOpcode is 23 or 24 or 28 or 29, $"{label}: unexpected access-chain type %{typeId}");
                    typeId = type[2];
                }
            }
        }
    }
}
