using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SHARED IL READER THE ARCHITECTURE ROWS WALK OVER: given a method, which methods its body calls.
    ///
    /// <para><b>WHY THESE RULES NEED IL AT ALL.</b> A field-graph walk answers "can this type be REACHED",
    /// which is what <c>V-D2</c>'s descriptor-pool rule needs. It cannot answer "does this path make this
    /// CALL", and two of this backend's rules are exactly that: M-N5 (no path reaches an <c>objc_msgSend</c>
    /// without a pool on it, <see cref="MetalAutoreleaseArchitectureTests"/>) and row 10's name blindness
    /// (nothing on <c>MetalShaderIndexTable</c> reads a layout element's name,
    /// <see cref="MetalIndexTableNameBlindnessTests"/>). The pool and the message send are two calls from one
    /// method with no type relationship between them, so the call site is the only thing that carries the fact.
    /// </para>
    ///
    /// <para><b>EXTRACTED RATHER THAN DUPLICATED</b>, which is what
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/594 asked for when the second rule needed the same
    /// hundred lines. Every caller keeps its own notion of what a violation is: this type only reads IL and
    /// never decides anything.</para>
    ///
    /// <para><b>IT FAILS QUIET AND WEAK, NEVER LOUD AND WRONG.</b> An opcode the table does not know, a body
    /// that cannot be read, a token that cannot be resolved: each ends the read for that one method rather than
    /// guessing, because a misaligned walk resolves garbage tokens. That can only make a rule weaker for one
    /// method, which is why every rule built on this ships a positive control proving the walk still finds what
    /// it claims to look for.</para>
    /// </summary>
    internal static class IlCallGraph
    {
        static readonly Dictionary<MethodBase, MethodBase[]> _callees = new();

        /// <summary>Every method <paramref name="method"/>'s body calls, cached per method.</summary>
        internal static MethodBase[] Callees(MethodBase method)
        {
            lock (_callees)
            {
                if (_callees.TryGetValue(method, out MethodBase[]? cached)) return cached;
            }

            MethodBase[] found = ReadCallees(method).ToArray();
            lock (_callees) { _callees[method] = found; }
            return found;
        }

        /// <summary>Short <c>Type.Member</c> rendering, for a violation message and for test output.</summary>
        internal static string Describe(MethodBase method)
            => (method.DeclaringType?.Name ?? "?") + "." + method.Name;

        /// <summary>Every method and constructor <paramref name="type"/> declares itself.</summary>
        internal static IEnumerable<MethodBase> DeclaredMethods(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (MethodInfo method in type.GetMethods(flags)) yield return method;
            foreach (ConstructorInfo ctor in type.GetConstructors(flags)) yield return ctor;
        }

        static IEnumerable<MethodBase> ReadCallees(MethodBase method)
        {
            byte[]? il = TryReadIl(method);
            if (il is null) yield break;

            Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
            Type[]? methodArgs = method is MethodInfo { IsGenericMethodDefinition: true } mi
                ? mi.GetGenericArguments()
                : null;

            int i = 0;
            while (i < il.Length)
            {
                short code = il[i];
                if (il[i] == 0xFE)
                {
                    if (i + 1 >= il.Length) yield break;
                    code = unchecked((short)(0xFE00 | il[i + 1]));
                    i += 2;
                }
                else
                {
                    i += 1;
                }

                // An opcode this table does not know means the walk has lost alignment, and a misaligned walk
                // resolves garbage tokens. Stopping is the only safe answer, and it can only make the rule
                // weaker for that one method rather than wrong for the assembly.
                if (!Opcodes.TryGetValue(code, out OpCode op)) yield break;

                if (op.OperandType == OperandType.InlineSwitch)
                {
                    if (i + 4 > il.Length) yield break;
                    int cases = BitConverter.ToInt32(il, i);
                    i += 4 + (4 * cases);
                    continue;
                }

                int operand = OperandSize(op.OperandType);
                if (i + operand > il.Length) yield break;

                if (IsCallSite(op))
                {
                    MethodBase? callee = TryResolve(method.Module, BitConverter.ToInt32(il, i), typeArgs,
                        methodArgs);
                    if (callee is not null) yield return callee;
                }

                i += operand;
            }
        }

        static byte[]? TryReadIl(MethodBase method)
        {
            try
            {
                return method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException
                or InvalidOperationException)
            {
                // An abstract, extern or runtime-provided method has no body to read, and neither does a
                // generated P/Invoke stub on some runtimes. None of those can call anything.
                return null;
            }
        }

        static bool IsCallSite(OpCode op)
            => op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj
                || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn;

        static MethodBase? TryResolve(Module module, int token, Type[]? typeArgs, Type[]? methodArgs)
        {
            try
            {
                return module.ResolveMethod(token, typeArgs, methodArgs);
            }
            catch (Exception ex) when (ex is ArgumentException or MissingMethodException
                or BadImageFormatException)
            {
                // A token that names a constructed generic this context cannot close. Skipping it can only lose
                // an edge, and every caller's positive control is what proves the walk still finds the edges
                // that matter.
                return null;
            }
        }

        static int OperandSize(OperandType type) => type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            _ => 4,
        };

        // Built from the runtime's own opcode table rather than transcribed, so a walk over IL this repo does
        // not generate itself cannot drift from the real operand widths.
        static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodes();

        static Dictionary<short, OpCode> BuildOpcodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is OpCode op) map[op.Value] = op;
            }
            return map;
        }
    }
}
