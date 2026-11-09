using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Bounded test execution of the actual coverage SPIR-V modules over poisoned word buffers.
/// Unsupported executed instructions refuse. This is bounded structural evidence for finite fixtures,
/// not a numerical or synchronization oracle. Host arithmetic and a serial lane scheduler do not model a device.</summary>
internal sealed class CoverageSpirvExecutor
{
    sealed record Type(uint Op, uint[] Data);
    sealed record Pointer(uint[] Words, int Offset, uint Type);
    sealed class Function
    {
        internal string Name = "";
        internal long Calls, Instructions, ExactInstructions;
        internal readonly List<(uint Type, uint Id)> Parameters = [];
        internal readonly List<uint[]> Code = [];
        internal readonly Dictionary<uint, int> Labels = [];
    }
    sealed class Frame(Function function)
    {
        internal readonly Function Function = function;
        internal bool ExactScope;
        internal readonly Dictionary<uint, object> Values = [];
        internal object Result = Array.Empty<uint>();
    }
    readonly Dictionary<uint, Type> types = [];
    readonly Dictionary<uint, uint> valueTypes = [];
    readonly Dictionary<uint, uint[]> constants = [];
    readonly Dictionary<uint, Function> functions = [];
    readonly Dictionary<uint, (uint Type, uint Storage, uint Initial)> variables = [];
    readonly Dictionary<uint, uint> bindings = [], builtins = [];
    readonly Dictionary<uint, object> globals = [];
    readonly Dictionary<uint, int> sizes = [];
    readonly IReadOnlyList<uint[]> buffers;
    readonly uint entry;
    int steps;
    const int StepLimit = 10_000_000;
    internal readonly record struct FunctionCount(string Name, long Calls, long Instructions, long ExactInstructions);
    internal IEnumerable<FunctionCount> InstructionProfile => functions.Values
        .Select(f => new FunctionCount(f.Name, f.Calls, f.Instructions, f.ExactInstructions));

    internal CoverageSpirvExecutor(string source, string name, IReadOnlyList<uint[]> buffers)
        : this(SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, name,
            new GpuShaderCompileOptions(PreciseMath: true)), buffers)
    { }

    internal CoverageSpirvExecutor(byte[] module, IReadOnlyList<uint[]> buffers)
    {
        this.buffers = buffers;
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        Function? function = null;
        var names = new Dictionary<uint, string>();
        for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
        {
            int length = (int)(words[at] >> 16);
            uint[] op = words.AsSpan(at, length).ToArray();
            uint code = op[0] & 65535;
            if (code == 5) names[op[1]] = Encoding.UTF8.GetString(MemoryMarshal.AsBytes(op.AsSpan(2))).TrimEnd('\0').Split('(')[0];
            if (code == 15) entry = op[2];
            if (code is >= 19 and <= 33) types.Add(op[1], new(code, op[2..]));
            else if (op.Length > 2 && types.ContainsKey(op[1])) valueTypes[op[2]] = op[1];
            if (code == 71 && op[2] == 33) bindings[op[1]] = op[3];
            if (code == 71 && op[2] == 11) builtins[op[1]] = op[3];
            if (code is 41 or 42 or 43 or 44 or 46)
                constants[op[2]] = code switch
                {
                    41 => [1],
                    42 => [0],
                    43 => op[3..],
                    44 => op[3..].SelectMany(id => constants[id]).ToArray(),
                    _ => new uint[Size(op[1])],
                };
            if (code == 54) { function = new() { Name = names.GetValueOrDefault(op[2], op[2].ToString()) }; functions.Add(op[2], function); }
            else if (code == 55) function!.Parameters.Add((op[1], op[2]));
            else if (code == 56) function = null;
            else if (function is not null)
            {
                if (code == 248) function.Labels.Add(op[1], function.Code.Count);
                function.Code.Add(op);
            }
            else if (code == 59) variables.Add(op[2], (types[op[1]].Data[1], op[3], op.Length > 4 ? op[4] : 0));
        }
    }

    int Size(uint id)
    {
        if (sizes.TryGetValue(id, out int value)) return value;
        Type type = types[id];
        value = type.Op switch
        {
            19 => 0,
            20 or 21 or 22 => 1,
            23 or 24 => checked(Size(type.Data[0]) * (int)type.Data[1]),
            28 => checked(Size(type.Data[0]) * (int)constants[type.Data[1]][0]),
            29 => 0,
            30 => type.Data.Sum(Size),
            32 => 1,
            _ => throw new NotSupportedException("Coverage test type " + type.Op),
        };
        sizes.Add(id, value); return value;
    }
    static object Copy(object value) => value is uint[] words ? words.ToArray() : value;
    object Value(Frame frame, uint id) => frame.Values.TryGetValue(id, out var value) ? value
        : constants.TryGetValue(id, out var constant) ? constant : globals[id];
    uint[] Words(Frame frame, uint id) => (uint[])Value(frame, id);
    uint Word(Frame frame, uint id) => Words(frame, id)[0];
    static uint[] Read(Pointer pointer, int count) => pointer.Words.AsSpan(pointer.Offset, count).ToArray();
    static void Write(Pointer pointer, uint[] words) => words.CopyTo(pointer.Words, pointer.Offset);
    static float Float(uint word) => BitConverter.UInt32BitsToSingle(word);
    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    static uint SignedRemainder(uint left, uint right, bool modulo)
    {
        int a = unchecked((int)left), b = unchecked((int)right);
        if (b == 0 || a == int.MinValue && b == -1)
            throw new NotSupportedException("Undefined or unsupported signed division in the bounded executor.");
        int remainder = a % b;
        if (modulo && remainder != 0 && (remainder < 0) != (b < 0)) remainder += b;
        return unchecked((uint)remainder);
    }

    internal void Run(int vertices)
    {
        if (vertices is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(vertices));
        RunGroups(1);
    }

    internal void RunGroups(int groups)
    {
        if (groups is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(groups));
        for (uint group = 0; group < groups; group++) RunGroup(group, (uint)groups);
    }

    void RunGroup(uint group, uint groups)
    {
        steps = 0;
        foreach (var (id, variable) in variables)
        {
            if (variable.Storage == 4)
                globals[id] = new Pointer(new uint[Size(variable.Type)], 0, variable.Type);
            else if (bindings.TryGetValue(id, out uint binding))
                globals[id] = new Pointer(buffers[(int)binding], 0, variable.Type);
        }
        var lanes = new IEnumerator<bool>[64];
        for (uint lane = 0; lane < lanes.Length; lane++)
        {
            var frame = new Frame(functions[entry]);
            foreach (var (id, variable) in variables)
            {
                if (variable.Storage is 4 or 2 or 12) continue;
                uint[] data = new uint[Size(variable.Type)];
                if (builtins.TryGetValue(id, out uint builtin))
                {
                    if (builtin == 28) data[0] = group * 64 + lane;
                    else if (builtin is 27 or 29) data[0] = lane;
                    else if (builtin == 26) data[0] = group;
                    else if (builtin == 24) { data[0] = groups; data[1] = 1; data[2] = 1; }
                    else throw new NotSupportedException("Coverage test builtin " + builtin);
                }
                else if (variable.Initial != 0) constants[variable.Initial].CopyTo(data, 0);
                frame.Values[id] = new Pointer(data, 0, variable.Type);
            }
            lanes[lane] = Execute(frame).GetEnumerator();
        }
        while (true)
        {
            int barriers = 0, completed = 0;
            foreach (var lane in lanes)
                if (lane.MoveNext()) barriers++;
                else completed++;
            if (completed == lanes.Length) break;
            if (barriers != lanes.Length) throw new InvalidOperationException("Nonuniform coverage publication barrier.");
        }
        foreach (var lane in lanes) lane.Dispose();
    }

    IEnumerable<bool> Execute(Frame frame)
    {
        frame.Function.Calls++;
        var code = frame.Function.Code;
        uint label = 0, previous = 0;
        for (int pc = 0; pc < code.Count; pc++)
        {
            if (++steps > StepLimit) throw new InvalidOperationException("Coverage test instruction bound exceeded.");
            frame.Function.Instructions++;
            if (frame.ExactScope) frame.Function.ExactInstructions++;
            uint[] op = code[pc];
            uint instruction = op[0] & 65535;
            void Set(object value) => frame.Values[op[2]] = value;
            uint[] Binary(Func<uint, uint, uint> apply)
            {
                var a = Words(frame, op[3]); var b = Words(frame, op[4]);
                return Enumerable.Range(0, Size(op[1])).Select(i => apply(a[i % a.Length], b[i % b.Length])).ToArray();
            }
            switch (instruction)
            {
                case 8: case 246: case 247: break;
                case 248: label = op[1]; break;
                case 59:
                    uint localType = types[op[1]].Data[1];
                    var local = new uint[Size(localType)];
                    if (op.Length > 4) Words(frame, op[4]).CopyTo(local, 0);
                    frame.Values[op[2]] = new Pointer(local, 0, localType); break;
                case 61:
                    Set(Read((Pointer)Value(frame, op[3]), Size(op[1]))); break;
                case 62:
                    Write((Pointer)Value(frame, op[1]), Words(frame, op[2])); break;
                case 63:
                    var destination = (Pointer)Value(frame, op[1]);
                    Write(destination, Read((Pointer)Value(frame, op[2]), Size(destination.Type))); break;
                case 65:
                case 66:
                    var pointer = (Pointer)Value(frame, op[3]);
                    int offset = pointer.Offset; uint typeId = pointer.Type;
                    for (int index = 4; index < op.Length; index++)
                    {
                        int member = checked((int)Word(frame, op[index]));
                        Type type = types[typeId];
                        if (type.Op == 30)
                        { offset += type.Data.Take(member).Sum(Size); typeId = type.Data[member]; }
                        else if (type.Op is 23 or 24 or 28 or 29)
                        { typeId = type.Data[0]; offset += checked(member * Size(typeId)); }
                        else throw new NotSupportedException("Coverage test access type " + type.Op);
                    }
                    Set(new Pointer(pointer.Words, offset, typeId)); break;
                case 57:
                    var child = new Frame(functions[op[3]])
                    { ExactScope = frame.ExactScope || functions[op[3]].Name == "covExactWorker" };
                    for (int i = 0; i < child.Function.Parameters.Count; i++)
                        child.Values[child.Function.Parameters[i].Id] = Copy(Value(frame, op[4 + i]));
                    // Input globals are invocation-local. Shared and storage variables remain shared.
                    foreach (var (id, variable) in variables)
                        if (frame.Values.TryGetValue(id, out var global)) child.Values[id] = global;
                    foreach (bool barrier in Execute(child)) yield return barrier;
                    Set(Copy(child.Result)); break;
                case 79:
                    uint[] combined = [.. Words(frame, op[3]), .. Words(frame, op[4])];
                    Set(op[5..].Select(i => combined[(int)i]).ToArray()); break;
                case 80: Set(op[3..].SelectMany(id => Words(frame, id)).ToArray()); break;
                case 81:
                    var composite = Words(frame, op[3]); int componentOffset = 0;
                    uint compositeType = valueTypes[op[3]];
                    for (int i = 4; i < op.Length; i++)
                    {
                        Type part = types[compositeType]; int member = (int)op[i];
                        if (part.Op == 30) { componentOffset += part.Data.Take(member).Sum(Size); compositeType = part.Data[member]; }
                        else { compositeType = part.Data[0]; componentOffset += member * Size(compositeType); }
                    }
                    Set(composite.AsSpan(componentOffset, Size(op[1])).ToArray()); break;
                case 82: throw new NotSupportedException("Coverage test composite insertion");
                case 83: case 124: case 113: case 114: case 115: Set(Copy(Value(frame, op[3]))); break;
                case 109: Set(Words(frame, op[3]).Select(v => (uint)Float(v)).ToArray()); break;
                case 110: Set(Words(frame, op[3]).Select(v => unchecked((uint)(int)Float(v))).ToArray()); break;
                case 111: Set(Words(frame, op[3]).Select(v => Bits((int)v)).ToArray()); break;
                case 112: Set(Words(frame, op[3]).Select(v => Bits(v)).ToArray()); break;
                case 126: Set(Words(frame, op[3]).Select(v => unchecked(0u - v)).ToArray()); break;
                case 127: Set(Words(frame, op[3]).Select(v => v ^ 0x80000000u).ToArray()); break;
                case 128: Set(Binary((a, b) => unchecked(a + b))); break;
                case 130: Set(Binary((a, b) => unchecked(a - b))); break;
                case 132: Set(Binary((a, b) => unchecked(a * b))); break;
                case 134: Set(Binary((a, b) => a / b)); break;
                case 135: Set(Binary((a, b) => unchecked((uint)((int)a / (int)b)))); break;
                case 137: Set(Binary((a, b) => a % b)); break;
                case 138: Set(Binary((a, b) => SignedRemainder(a, b, modulo: false))); break;
                case 139: Set(Binary((a, b) => SignedRemainder(a, b, modulo: true))); break;
                case 129: Set(Binary((a, b) => Bits(Float(a) + Float(b)))); break;
                case 131: Set(Binary((a, b) => Bits(Float(a) - Float(b)))); break;
                case 133: Set(Binary((a, b) => Bits(Float(a) * Float(b)))); break;
                case 136: Set(Binary((a, b) => Bits(Float(a) / Float(b)))); break;
                case 164: case 170: Set(Binary((a, b) => a == b ? 1u : 0)); break;
                case 165: case 171: Set(Binary((a, b) => a != b ? 1u : 0)); break;
                case 166: Set(Binary((a, b) => a != 0 || b != 0 ? 1u : 0)); break;
                case 167: Set(Binary((a, b) => a != 0 && b != 0 ? 1u : 0)); break;
                case 168: Set(Words(frame, op[3]).Select(v => v == 0 ? 1u : 0).ToArray()); break;
                case 169:
                    var condition = Words(frame, op[3]); var yes = Value(frame, op[4]); var no = Value(frame, op[5]);
                    if (yes is Pointer) Set(condition[0] != 0 ? yes : no);
                    else Set(Enumerable.Range(0, Size(op[1])).Select(i => condition[i % condition.Length] != 0
                        ? ((uint[])yes)[i] : ((uint[])no)[i]).ToArray());
                    break;
                case 172: Set(Binary((a, b) => a > b ? 1u : 0)); break;
                case 173: Set(Binary((a, b) => (int)a > (int)b ? 1u : 0)); break;
                case 174: Set(Binary((a, b) => a >= b ? 1u : 0)); break;
                case 175: Set(Binary((a, b) => (int)a >= (int)b ? 1u : 0)); break;
                case 176: Set(Binary((a, b) => a < b ? 1u : 0)); break;
                case 177: Set(Binary((a, b) => (int)a < (int)b ? 1u : 0)); break;
                case 178: Set(Binary((a, b) => a <= b ? 1u : 0)); break;
                case 179: Set(Binary((a, b) => (int)a <= (int)b ? 1u : 0)); break;
                case 180: Set(Binary((a, b) => Float(a) == Float(b) ? 1u : 0)); break;
                case 182:
                    Set(Binary((a, b) => !float.IsNaN(Float(a)) && !float.IsNaN(Float(b))
                    && Float(a) != Float(b) ? 1u : 0)); break;
                case 183: Set(Binary((a, b) => Float(a) != Float(b) ? 1u : 0)); break;
                case 184: Set(Binary((a, b) => Float(a) < Float(b) ? 1u : 0)); break;
                case 186: Set(Binary((a, b) => Float(a) > Float(b) ? 1u : 0)); break;
                case 188: Set(Binary((a, b) => Float(a) <= Float(b) ? 1u : 0)); break;
                case 190: Set(Binary((a, b) => Float(a) >= Float(b) ? 1u : 0)); break;
                case 194: Set(Binary((a, b) => a >> (int)b)); break;
                case 195: Set(Binary((a, b) => unchecked((uint)((int)a >> (int)b)))); break;
                case 196: Set(Binary((a, b) => a << (int)b)); break;
                case 197: Set(Binary((a, b) => a | b)); break;
                case 198: Set(Binary((a, b) => a ^ b)); break;
                case 199: Set(Binary((a, b) => a & b)); break;
                case 200: Set(Words(frame, op[3]).Select(v => ~v).ToArray()); break;
                case 224: yield return true; break;
                case 237:
                case 239:
                case 241:
                    var atomic = (Pointer)Value(frame, op[3]);
                    uint old = atomic.Words[atomic.Offset], operand = Word(frame, op[6]);
                    atomic.Words[atomic.Offset] = instruction == 237 ? Math.Min(old, operand)
                        : instruction == 239 ? Math.Max(old, operand) : old | operand;
                    Set(new uint[] { old }); break;
                case 245:
                    for (int i = 3; i < op.Length; i += 2)
                        if (op[i + 1] == previous) { Set(Copy(Value(frame, op[i]))); break; }
                    break;
                case 249: previous = label; pc = frame.Function.Labels[op[1]] - 1; break;
                case 250:
                    previous = label; pc = frame.Function.Labels[Word(frame, op[1]) != 0 ? op[2] : op[3]] - 1; break;
                case 253: yield break;
                case 254: frame.Result = Copy(Value(frame, op[1])); yield break;
                case 12:
                    uint[] left = Words(frame, op[5]);
                    uint[] right = op.Length > 6 ? Words(frame, op[6]) : left;
                    Set(Enumerable.Range(0, Size(op[1])).Select(i =>
                    {
                        uint a = left[i % left.Length], b = right[i % right.Length];
                        return op[4] switch
                        {
                            4 => a & 0x7fffffffu,
                            31 => Bits(MathF.Sqrt(Float(a))),
                            37 => Bits(MathF.Min(Float(a), Float(b))),
                            38 => Math.Min(a, b),
                            40 => Bits(MathF.Max(Float(a), Float(b))),
                            41 => Math.Max(a, b),
                            74 => unchecked((uint)(31 - BitOperations.LeadingZeroCount((int)a < 0 ? ~a : a))),
                            75 => unchecked((uint)(31 - BitOperations.LeadingZeroCount(a))),
                            _ => throw new NotSupportedException("Coverage test extended instruction " + op[4]),
                        };
                    }).ToArray()); break;
                default: throw new NotSupportedException("Coverage test instruction " + instruction);
            }
        }
    }
}
