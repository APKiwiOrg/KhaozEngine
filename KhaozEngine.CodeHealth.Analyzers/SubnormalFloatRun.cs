using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
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
        private readonly ConcurrentDictionary<ISymbol, Location> _subnormalBitFields =
            new ConcurrentDictionary<ISymbol, Location>(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<ISymbol, byte> _fieldsReadAsFloat =
            new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<ISymbol, byte> _steppedTypeParameters =
            new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
        private readonly ConcurrentBag<GenericUse> _genericUses = new ConcurrentBag<GenericUse>();

        public SubnormalFloatRun(Compilation compilation)
        {
            _mathF = compilation.GetTypeByMetadataName("System.MathF");
            _bitConverter = compilation.GetTypeByMetadataName("System.BitConverter");
            _unsafe = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.Unsafe");
            _ieee754 = compilation.GetTypeByMetadataName("System.Numerics.IFloatingPointIeee754`1");
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
        }

        public void CheckMethodReference(OperationAnalysisContext context)
        {
            var reference = (IMethodReferenceOperation)context.Operation;
            CheckMethod(context, reference.Method, reference.Syntax);
        }

        public void RecordFieldInitializer(OperationAnalysisContext context)
        {
            var initializer = (IFieldInitializerOperation)context.Operation;
            var constant = initializer.Value.ConstantValue;
            if (!constant.HasValue || !SubnormalBits.TryGetSingleBits(constant.Value, out var bits)
                || !SubnormalBits.IsSubnormal(bits)) return;
            foreach (var field in initializer.InitializedFields)
            {
                // The JIT folds a static readonly field once its class is initialized. A mutable static stays a load.
                if (field.IsStatic && field.IsReadOnly && !field.IsConst && field.Locations.Length > 0)
                    _subnormalBitFields.TryAdd(field.OriginalDefinition, field.Locations[0]);
            }
        }

        public void ReportDeferred(CompilationAnalysisContext context)
        {
            foreach (var field in _subnormalBitFields)
            {
                if (!_fieldsReadAsFloat.ContainsKey(field.Key)) continue;
                context.ReportDiagnostic(Diagnostic.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant, field.Value,
                    $"The static readonly field '{field.Key.Name}' holds the bits of a subnormal float that are read as a float"));
            }

            if (_steppedTypeParameters.IsEmpty) return;
            var stepped = new HashSet<ISymbol>(_steppedTypeParameters.Keys, SymbolEqualityComparer.Default);
            var uses = _genericUses.ToArray();
            // A generic that passes its own type parameter on to a stepping generic steps that parameter too.
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var use in uses)
                {
                    foreach (var pair in use.Pairs)
                    {
                        if (pair.Argument is ITypeParameterSymbol forwarded && stepped.Contains(pair.Parameter)
                            && stepped.Add(forwarded.OriginalDefinition)) grew = true;
                    }
                }
            }
            foreach (var use in uses)
            {
                var floatStep = use.Pairs.FirstOrDefault(pair =>
                    pair.Argument.SpecialType == SpecialType.System_Single && stepped.Contains(pair.Parameter));
                if (floatStep.Parameter is null) continue;
                context.ReportDiagnostic(Diagnostic.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant, use.Location,
                    $"'{floatStep.Parameter.ContainingSymbol.Name}' steps its type parameter '{floatStep.Parameter.Name}' " +
                    "with BitDecrement or BitIncrement and is used here with float"));
            }
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
                    _steppedTypeParameters.TryAdd(self.OriginalDefinition, 0);
            }
            RecordGenericUse(method, syntax);
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
            RecordFieldsReadAsFloat(argument);
        }

        // Follows the paths a value takes into the read: conversions and the branches of a conditional, a switch
        // expression or a coalesce. A field that only feeds arithmetic, such as a mask, is not read as a float.
        private void RecordFieldsReadAsFloat(IOperation value)
        {
            switch (value)
            {
                case IConversionOperation conversion:
                    RecordFieldsReadAsFloat(conversion.Operand);
                    break;
                case IConditionalOperation conditional:
                    RecordFieldsReadAsFloat(conditional.WhenTrue);
                    if (conditional.WhenFalse != null) RecordFieldsReadAsFloat(conditional.WhenFalse);
                    break;
                case ICoalesceOperation coalesce:
                    RecordFieldsReadAsFloat(coalesce.Value);
                    RecordFieldsReadAsFloat(coalesce.WhenNull);
                    break;
                case ISwitchExpressionOperation switchExpression:
                    foreach (var arm in switchExpression.Arms) RecordFieldsReadAsFloat(arm.Value);
                    break;
                case IFieldReferenceOperation field:
                    _fieldsReadAsFloat.TryAdd(field.Field.OriginalDefinition, 0);
                    break;
            }
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

        private void RecordGenericUse(IMethodSymbol method, SyntaxNode syntax)
        {
            List<TypeArgumentPair>? pairs = null;
            if (method.IsGenericMethod)
                AddPairs(ref pairs, method.OriginalDefinition.TypeParameters, method.TypeArguments);
            for (var type = method.ContainingType; type != null; type = type.ContainingType)
            {
                if (type.IsGenericType)
                    AddPairs(ref pairs, type.OriginalDefinition.TypeParameters, type.TypeArguments);
            }
            if (pairs != null) _genericUses.Add(new GenericUse(pairs.ToArray(), syntax.GetLocation()));
        }

        private static void AddPairs(ref List<TypeArgumentPair>? pairs,
            ImmutableArray<ITypeParameterSymbol> parameters, ImmutableArray<ITypeSymbol> arguments)
        {
            for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
            {
                var argument = arguments[i];
                if (argument.SpecialType != SpecialType.System_Single && !(argument is ITypeParameterSymbol)) continue;
                if (SymbolEqualityComparer.Default.Equals(argument, parameters[i])) continue;
                (pairs ??= new List<TypeArgumentPair>()).Add(new TypeArgumentPair(parameters[i], argument));
            }
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

        private readonly struct TypeArgumentPair
        {
            public TypeArgumentPair(ITypeParameterSymbol parameter, ITypeSymbol argument)
            {
                Parameter = parameter;
                Argument = argument;
            }

            public ITypeParameterSymbol Parameter { get; }
            public ITypeSymbol Argument { get; }
        }

        private sealed class GenericUse
        {
            public GenericUse(TypeArgumentPair[] pairs, Location location)
            {
                Pairs = pairs;
                Location = location;
            }

            public TypeArgumentPair[] Pairs { get; }
            public Location Location { get; }
        }
    }

    /// <summary>Subnormal tests on a float value and on 32-bit patterns read as a float.</summary>
    internal static class SubnormalBits
    {
        // The smallest normal float, 2^-126. Compared in the analyzer's own process, which runs at the default controls.
        private const float SmallestNormal = 1.17549435E-38f;

        public static bool IsSubnormal(float value) => value != 0f && Math.Abs(value) < SmallestNormal;

        public static bool IsSubnormal(uint bits) => (bits & 0x7F800000u) == 0 && (bits & 0x007FFFFFu) != 0;

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
