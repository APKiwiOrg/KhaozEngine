using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Declares the binary32 IEEE exceptional-value policy on each SPIR-V entry point.</summary>
    internal static class SpirvPreciseMath
    {
        // SPV_KHR_float_controls works with the pinned SPIR-V 1.0 module. No IDs or dataflow change.
        internal static byte[] Declare(byte[] spirv, bool requireEft = false, bool flushDenorms = false)
        {
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
            var entries = new List<uint>();
            int capabilityEnd = 5, extensionEnd = 5, modeEnd = 5;
            bool hasExtension = false;
            var capabilities = new HashSet<uint>();
            var declared = new HashSet<(uint Entry, uint Mode)>();
            (uint Mode, uint Capability)[] modes = requireEft
                ? [(4461u, 4466u), (4462u, 4467u), (flushDenorms ? 4460u : 4459u, flushDenorms ? 4465u : 4464u)]
                : [(4461u, 4466u)];
            for (int offset = 5; offset < words.Length;)
            {
                int length = (int)(words[offset] >> 16);
                uint opcode = words[offset] & 0xffffu;
                if (length == 0 || offset + length > words.Length)
                    throw new ShaderValidationException("Malformed SPIR-V while declaring precise float controls.");
                if (opcode == 17)
                {
                    capabilityEnd = offset + length;
                    extensionEnd = capabilityEnd;
                    capabilities.Add(words[offset + 1]);
                }
                if (opcode == 10)
                {
                    extensionEnd = offset + length;
                    hasExtension |= Encoding.UTF8.GetString(MemoryMarshal.AsBytes(words.Slice(offset + 1, length - 1)))
                        .TrimEnd('\0') == "SPV_KHR_float_controls";
                }
                if (opcode is 17 or 10 or 11 or 14 or 15 or 16 or 331) modeEnd = offset + length;
                if (opcode == 15) entries.Add(words[offset + 2]);
                if (opcode == 16 && length == 4 && words[offset + 3] == 32u)
                    declared.Add((words[offset + 1], words[offset + 2]));
                offset += length;
            }
            var output = new List<uint>(words.ToArray());
            foreach (uint entry in entries)
                foreach (var mode in modes)
                    if (!declared.Contains((entry, mode.Mode)))
                        output.InsertRange(modeEnd, new uint[] { (4u << 16) | 16u, entry, mode.Mode, 32u });
            if (!hasExtension && words[1] < 0x00010400u)
            {
                byte[] name = Encoding.UTF8.GetBytes("SPV_KHR_float_controls\0");
                Array.Resize(ref name, (name.Length + 3) & ~3);
                var instruction = new List<uint> { ((uint)(1 + name.Length / 4) << 16) | 10u };
                instruction.AddRange(MemoryMarshal.Cast<byte, uint>(name).ToArray());
                output.InsertRange(extensionEnd, instruction);
            }
            foreach (var mode in modes)
                if (!capabilities.Contains(mode.Capability))
                    output.InsertRange(capabilityEnd, new uint[] { (2u << 16) | 17u, mode.Capability });
            return MemoryMarshal.AsBytes(output.ToArray().AsSpan()).ToArray();
        }
    }
}
