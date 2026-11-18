using System;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// One compilation's KEFP001 checks. Direct forms report at once. A static readonly field read as a float and a
    /// generic that steps its type parameter need the whole compilation, so they report at compilation end.
    /// </summary>
    internal sealed class SubnormalFloatRun
    {
        private readonly INamedTypeSymbol? _mathF;
        private readonly INamedTypeSymbol? _bitConverter;
        private readonly INamedTypeSymbol? _unsafe;
        private readonly INamedTypeSymbol? _ieee754;
        private readonly INamedTypeSymbol? _helper;
        private readonly INamedTypeSymbol? _helperTemplate;
        private readonly SubnormalFloatFieldReads _fieldReads;
        private readonly SubnormalFloatGenericUses _genericUses;
        private readonly SubnormalFloatSelectedUses _selectedUses;

        public SubnormalFloatRun(Compilation compilation)
        {
            _mathF = compilation.GetTypeByMetadataName("System.MathF");
            _bitConverter = compilation.GetTypeByMetadataName("System.BitConverter");
            _unsafe = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.Unsafe");
            _ieee754 = compilation.GetTypeByMetadataName("System.Numerics.IFloatingPointIeee754`1");
            // Match the namespace written by FloatUlp.targets, or the unmodified template used as source.
            var assemblyNamespace = Regex.Replace(compilation.AssemblyName ?? "", "[^A-Za-z0-9_.]", "_");
            _helper = compilation.Assembly.GetTypeByMetadataName(assemblyNamespace + ".FloatSteps.FloatUlp");
            _helperTemplate = compilation.Assembly.GetTypeByMetadataName("KhaozEngine.FloatSteps.FloatUlp");
            _fieldReads = new SubnormalFloatFieldReads(compilation.Options.CheckOverflow);
            _genericUses = new SubnormalFloatGenericUses(compilation.Assembly);
            _selectedUses = new SubnormalFloatSelectedUses(_genericUses);
        }

        public void CheckConstant(OperationAnalysisContext context)
        {
            var operation = context.Operation;
            if (operation.Type?.SpecialType != SpecialType.System_Single) return;
            if (!(operation.ConstantValue is { HasValue: true, Value: float value }) || !SubnormalBits.IsSubnormal(value))
                return;
            // The compiler emits the outermost constant, so (double)float.Epsilon reaches the JIT as a normal double.
            if (operation.Parent is { ConstantValue: { HasValue: true } }) return;
            Report(context, operation.Syntax.GetLocation(), "A subnormal float constant reaches the JIT here");
        }

        public void CheckInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            CheckMethod(context, invocation.TargetMethod, invocation.Syntax);
            CheckBitsReadAsFloat(context, invocation);
            CheckConstantHelperStep(context, invocation);
        }

        public void CheckMethodReference(OperationAnalysisContext context)
        {
            var reference = (IMethodReferenceOperation)context.Operation;
            CheckMethod(context, reference.Method, reference.Syntax);
        }

        public void CheckObjectCreation(OperationAnalysisContext context)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            _genericUses.RecordUse(context, creation.Constructor);
        }

        public void CheckMemberReference(OperationAnalysisContext context) =>
            _genericUses.RecordUse(context, ((IMemberReferenceOperation)context.Operation).Member);

        public void CheckGenericName(SyntaxNodeAnalysisContext context)
        {
            var syntax = (GenericNameSyntax)context.Node;
            _genericUses.RecordName(context.SemanticModel.GetSymbolInfo(syntax, context.CancellationToken).Symbol, syntax);
        }

        public void CheckCompilerSelectedUse(OperationAnalysisContext context) => _selectedUses.Record(context);

        public void RecordFieldInitializer(OperationAnalysisContext context) => _fieldReads.RecordInitializer(context);

        public void ReportDeferred(CompilationAnalysisContext context)
        {
            _fieldReads.Report(context);
            _genericUses.Report(context);
        }

        private void CheckMethod(OperationAnalysisContext context, IMethodSymbol method, SyntaxNode syntax)
        {
            if (method.Name == "BitDecrement" || method.Name == "BitIncrement")
            {
                var owner = method.ContainingType;
                if (IsFloatStepOwner(owner))
                {
                    Report(context, syntax.GetLocation(),
                        $"{Describe(owner)}.{method.Name} is a BCL float step whose zero case holds a subnormal constant");
                    return;
                }
                if (IsIeee754(owner) && owner.TypeArguments[0] is ITypeParameterSymbol self)
                    _genericUses.RecordStep(self);
            }
            _genericUses.RecordUse(context, method);
        }

        private void CheckBitsReadAsFloat(OperationAnalysisContext context, IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            if (!ReadsBitsAsFloat(method) || invocation.Arguments.Length == 0) return;
            var argument = invocation.Arguments[0].Value;
            if (argument.ConstantValue.HasValue)
            {
                if (SubnormalBits.TryGetSingleBits(argument.ConstantValue.Value, out var bits) && SubnormalBits.IsSubnormal(bits))
                    Report(context, invocation.Syntax.GetLocation(),
                        $"{method.ContainingType.Name}.{method.Name} reads constant bits that are a subnormal float");
                return;
            }
            _fieldReads.RecordRead(argument);
        }

        private void CheckConstantHelperStep(OperationAnalysisContext context, IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            if ((method.Name != "Up" && method.Name != "Down") || !method.IsStatic || invocation.Arguments.Length != 1
                || method.ReturnType.SpecialType != SpecialType.System_Single
                || method.Parameters[0].Type.SpecialType != SpecialType.System_Single) return;
            var owner = method.ContainingType;
            if (!SymbolEqualityComparer.Default.Equals(owner, _helper)
                && !SymbolEqualityComparer.Default.Equals(owner, _helperTemplate)) return;
            if (!TryGetConstantBits(invocation.Arguments[0].Value, out var bits)
                || !SubnormalBits.StepFoldsToSubnormal(bits, up: method.Name == "Up")) return;
            Report(context, invocation.Syntax.GetLocation(),
                $"FloatUlp.{method.Name} steps a constant argument to a subnormal float");
        }

        private bool TryGetConstantBits(IOperation value, out uint bits)
        {
            bits = 0;
            if (value.ConstantValue is { HasValue: true, Value: float constant })
            {
                bits = BitConverter.ToUInt32(BitConverter.GetBytes(constant), 0);
                return true;
            }
            return value is IInvocationOperation invocation && ReadsBitsAsFloat(invocation.TargetMethod)
                && invocation.Arguments.Length > 0 && invocation.Arguments[0].Value.ConstantValue is { HasValue: true } input
                && SubnormalBits.TryGetSingleBits(input.Value, out bits);
        }

        private bool ReadsBitsAsFloat(IMethodSymbol method)
        {
            var owner = method.ContainingType;
            if (SymbolEqualityComparer.Default.Equals(owner, _bitConverter))
                return method.Name == "Int32BitsToSingle" || method.Name == "UInt32BitsToSingle";
            return SymbolEqualityComparer.Default.Equals(owner, _unsafe)
                && (method.Name == "BitCast" || method.Name == "As")
                && method.TypeArguments.Length == 2
                && method.TypeArguments[1].SpecialType == SpecialType.System_Single;
        }

        private bool IsFloatStepOwner(INamedTypeSymbol owner) =>
            SymbolEqualityComparer.Default.Equals(owner, _mathF)
            || owner.SpecialType == SpecialType.System_Single
            || (IsIeee754(owner) && owner.TypeArguments[0].SpecialType == SpecialType.System_Single);

        private bool IsIeee754(INamedTypeSymbol owner) =>
            _ieee754 != null && SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, _ieee754);

        private string Describe(INamedTypeSymbol owner) =>
            SymbolEqualityComparer.Default.Equals(owner, _mathF) ? "MathF"
            : owner.SpecialType == SpecialType.System_Single ? "float"
            : "IFloatingPointIeee754<float>";

        private static void Report(OperationAnalysisContext context, Location location, string form) =>
            context.ReportDiagnostic(Diagnostic.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant, location, form));
    }

    /// <summary>Subnormal tests on a float value and on 32-bit patterns read as a float.</summary>
    internal static class SubnormalBits
    {
        // The smallest normal float, 2^-126. Compared in the analyzer's own process, which runs at the default controls.
        private const float SmallestNormal = 1.17549435E-38f;

        public static bool IsSubnormal(float value) => value != 0f && Math.Abs(value) < SmallestNormal;

        public static bool IsSubnormal(uint bits) => (bits & 0x7F800000u) == 0 && (bits & 0x007FFFFFu) != 0;

        public static bool StepFoldsToSubnormal(uint bits, bool up)
        {
            uint magnitude = bits & 0x7FFFFFFFu;
            // Both zero signs take the mutable field path. Only nearby nonzero values can step into a subnormal.
            if (magnitude == 0 || magnitude > 0x00800000u) return false;
            bool towardZero = (bits >> 31 != 0) == up;
            return IsSubnormal(towardZero ? magnitude - 1 : magnitude + 1);
        }

        public static bool TryGetSingleBits(object? value, out uint bits)
        {
            switch (value)
            {
                case int signed:
                    bits = unchecked((uint)signed);
                    return true;
                case uint unsigned:
                    bits = unsigned;
                    return true;
                default:
                    bits = 0;
                    return false;
            }
        }
    }
}
