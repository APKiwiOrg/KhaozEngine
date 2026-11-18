using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Classifies readonly field values at the float reinterpretation, after integral and enum conversions.</summary>
    internal sealed class SubnormalFloatFieldReads
    {
        private readonly bool _checkOverflow;
        private readonly ConcurrentDictionary<IFieldSymbol, decimal> _initializers =
            new ConcurrentDictionary<IFieldSymbol, decimal>(SymbolEqualityComparer.Default);
        private readonly ConcurrentBag<FieldRead> _reads = new ConcurrentBag<FieldRead>();

        public SubnormalFloatFieldReads(bool checkOverflow) => _checkOverflow = checkOverflow;

        public void RecordInitializer(OperationAnalysisContext context)
        {
            var initializer = (IFieldInitializerOperation)context.Operation;
            if (!initializer.Value.ConstantValue.HasValue ||
                !TryGetIntegralValue(initializer.Value.ConstantValue.Value, out var value)) return;
            foreach (var field in initializer.InitializedFields)
            {
                // The JIT folds a static readonly field once its class is initialized. A mutable static stays a load.
                if (CanFold(field) && field.Locations.Length > 0)
                    _initializers.TryAdd(field.OriginalDefinition, value);
            }
        }

        public void RecordRead(IOperation argument)
        {
            var type = argument.Type?.SpecialType;
            if (type == SpecialType.System_Int32 || type == SpecialType.System_UInt32)
                RecordRead(argument, ImmutableArray<IntegralConversion>.Empty);
        }

        public void Report(CompilationAnalysisContext context)
        {
            var reported = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
            foreach (var read in _reads)
            {
                var field = read.Field;
                if (reported.Contains(field) || !_initializers.TryGetValue(field, out var value)) continue;
                if (!TryApplyConversions(read.Conversions, value, out var bits) || !SubnormalBits.IsSubnormal(bits)) continue;
                reported.Add(field);
                context.ReportDiagnostic(Diagnostic.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant,
                    field.Locations[0], $"The static readonly field '{field.Name}' is read as a subnormal float"));
            }
        }

        private static bool CanFold(IFieldSymbol field) => field.IsStatic && field.IsReadOnly && !field.IsConst;

        // Follow value branches and casts within the integral and enum domain. Arithmetic breaks this chain.
        // The traversal goes from the read to the field, so the conversions are applied in reverse order.
        private void RecordRead(IOperation value, ImmutableArray<IntegralConversion> conversions)
        {
            switch (value)
            {
                case IConversionOperation conversion when !conversion.Conversion.IsUserDefined:
                    if (TryGetIntegralType(conversion.Operand.Type, out _) &&
                        TryGetIntegralType(conversion.Type, out var target))
                    {
                        RecordRead(conversion.Operand, conversions.Add(new IntegralConversion(target, IsChecked(conversion))));
                    }
                    break;
                case IConditionalOperation conditional:
                    RecordRead(conditional.WhenTrue, conversions);
                    if (conditional.WhenFalse != null) RecordRead(conditional.WhenFalse, conversions);
                    break;
                case ICoalesceOperation coalesce:
                    if (!coalesce.ValueConversion.IsUserDefined &&
                        TryGetIntegralType(coalesce.Value.Type, out _) && TryGetIntegralType(coalesce.Type, out var result))
                    {
                        RecordRead(coalesce.Value, conversions.Add(new IntegralConversion(result, isChecked: false)));
                    }
                    RecordRead(coalesce.WhenNull, conversions);
                    break;
                case ISwitchExpressionOperation switchExpression:
                    foreach (var arm in switchExpression.Arms) RecordRead(arm.Value, conversions);
                    break;
                case IFieldReferenceOperation field when CanFold(field.Field):
                    _reads.Add(new FieldRead(field.Field.OriginalDefinition, conversions));
                    break;
            }
        }

        private bool IsChecked(IConversionOperation conversion)
        {
            if (conversion.IsChecked) return true;
            // Roslyn's IsChecked omits enum casts. The nearest overflow context applies to their underlying types.
            for (var syntax = conversion.Syntax; syntax != null; syntax = syntax.Parent)
            {
                switch (syntax.Kind())
                {
                    case SyntaxKind.CheckedExpression:
                    case SyntaxKind.CheckedStatement:
                        return true;
                    case SyntaxKind.UncheckedExpression:
                    case SyntaxKind.UncheckedStatement:
                        return false;
                }
            }
            return _checkOverflow;
        }

        private static bool TryGetIntegralType(ITypeSymbol? type, out SpecialType result)
        {
            if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                type = named.TypeArguments[0];
            type = (type as INamedTypeSymbol)?.EnumUnderlyingType ?? type;
            result = type?.SpecialType ?? SpecialType.None;
            return result is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Char
                or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
                or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;
        }

        private static bool TryGetIntegralValue(object? value, out decimal result)
        {
            switch (value)
            {
                case sbyte number: result = number; return true;
                case byte number: result = number; return true;
                case char number: result = number; return true;
                case short number: result = number; return true;
                case ushort number: result = number; return true;
                case int number: result = number; return true;
                case uint number: result = number; return true;
                case long number: result = number; return true;
                case ulong number: result = number; return true;
                default: result = 0; return false;
            }
        }

        private static bool TryApplyConversions(ImmutableArray<IntegralConversion> conversions, decimal value, out uint bits)
        {
            for (int i = conversions.Length - 1; i >= 0; i--)
            {
                var conversion = conversions[i];
                var converted = conversion.Apply(value);
                // An overflowing checked conversion throws before delivering any bits to the reinterpretation.
                if (conversion.IsChecked && converted != value)
                {
                    bits = 0;
                    return false;
                }
                value = converted;
            }
            bits = unchecked((uint)ToIntegerBits(value));
            return true;
        }

        private static ulong ToIntegerBits(decimal value) =>
            value < 0 ? unchecked((ulong)(long)value) : (ulong)value;

        private readonly struct IntegralConversion
        {
            public IntegralConversion(SpecialType target, bool isChecked)
            {
                Target = target;
                IsChecked = isChecked;
            }

            private SpecialType Target { get; }
            public bool IsChecked { get; }

            public decimal Apply(decimal value)
            {
                // Decimal preserves the full value of both long and ulong for checked overflow comparisons.
                // Unchecked integral casts truncate low bits and preserve the destination's signedness.
                ulong bits = ToIntegerBits(value);
                return Target switch
                {
                    SpecialType.System_SByte => unchecked((sbyte)bits),
                    SpecialType.System_Byte => unchecked((byte)bits),
                    SpecialType.System_Char or SpecialType.System_UInt16 => unchecked((ushort)bits),
                    SpecialType.System_Int16 => unchecked((short)bits),
                    SpecialType.System_Int32 => unchecked((int)bits),
                    SpecialType.System_UInt32 => unchecked((uint)bits),
                    SpecialType.System_Int64 => unchecked((long)bits),
                    _ => bits,
                };
            }
        }

        private readonly struct FieldRead
        {
            public FieldRead(IFieldSymbol field, ImmutableArray<IntegralConversion> conversions)
            {
                Field = field;
                Conversions = conversions;
            }

            public IFieldSymbol Field { get; }
            public ImmutableArray<IntegralConversion> Conversions { get; }
        }
    }
}
