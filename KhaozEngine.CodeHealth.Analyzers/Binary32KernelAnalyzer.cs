using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Closes attributed kernels over the hand-reviewed binary32 arithmetic home.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class Binary32KernelAnalyzer : DiagnosticAnalyzer
    {
        private const string KernelAttribute = "Binary32KernelAttribute";
        private const string ArithmeticAttribute = "Binary32ArithmeticAttribute";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            Binary32KernelDiagnostics.FloatingOperator, Binary32KernelDiagnostics.FloatingConversion,
            Binary32KernelDiagnostics.PlatformArithmetic, Binary32KernelDiagnostics.UnmarkedCall);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            // A home alone cannot make any member a kernel. Do no per-node work in unused consumers.
            // GetSymbolsWithName covers source declarations. TypeNames also permits shared attribute
            // assemblies without walking their namespace trees or resolving every metadata type.
            if (!context.Compilation.GetSymbolsWithName(KernelAttribute, SymbolFilter.Type, context.CancellationToken).Any()
                && !context.Compilation.SourceModule.ReferencedAssemblySymbols.Any(
                    assembly => assembly.TypeNames.Contains(KernelAttribute)))
                return;

            var state = new CompilationState();
            context.RegisterOperationAction(state.Analyze,
                OperationKind.Binary, OperationKind.Unary, OperationKind.Increment, OperationKind.Decrement,
                OperationKind.CompoundAssignment, OperationKind.Conversion, OperationKind.Invocation,
                OperationKind.PropertyReference);
        }

        private enum Membership { Unmarked, Kernel, Home }

        private sealed class CompilationState
        {
            private readonly ConcurrentDictionary<ISymbol, Membership> _membership =
                new ConcurrentDictionary<ISymbol, Membership>(SymbolEqualityComparer.Default);

            public void Analyze(OperationAnalysisContext context)
            {
                var owner = context.ContainingSymbol;
                // Operation callbacks can name the outer method for a local function or lambda.
                // Its own attribute still counts, even when the outer method is not attributed.
                for (var parent = context.Operation.Parent; parent != null; parent = parent.Parent)
                {
                    if (parent is IAnonymousFunctionOperation lambda) { owner = lambda.Symbol; break; }
                    if (parent is ILocalFunctionOperation local) { owner = local.Symbol; break; }
                }
                if (!(owner is IMethodSymbol) || GetMembership(owner) != Membership.Kernel) return;

                switch (context.Operation)
                {
                    case IBinaryOperation binary:
                        CheckOperator(context, binary.OperatorKind.ToString(), binary.OperatorMethod,
                            binary.LeftOperand.Type, binary.RightOperand.Type);
                        break;
                    case IUnaryOperation unary:
                        CheckOperator(context, unary.OperatorKind.ToString(), unary.OperatorMethod, unary.Operand.Type);
                        break;
                    case IIncrementOrDecrementOperation increment:
                        CheckOperator(context, increment.Kind.ToString(), increment.OperatorMethod, increment.Target.Type);
                        break;
                    case ICompoundAssignmentOperation compound:
                        CheckOperator(context, compound.OperatorKind.ToString(), compound.OperatorMethod,
                            compound.Target.Type, compound.Value.Type);
                        break;
                    case IConversionOperation conversion:
                        if (IsNumericConversion(conversion) && !conversion.ConstantValue.HasValue
                            && !conversion.Operand.ConstantValue.HasValue
                            && (IsFloating(conversion.Operand.Type) || IsFloating(conversion.Type)))
                            Report(context, Binary32KernelDiagnostics.FloatingConversion,
                                conversion.Operand.Type?.ToDisplayString() ?? "?", conversion.Type?.ToDisplayString() ?? "?");
                        else if (conversion.OperatorMethod != null)
                            CheckCall(context, conversion.OperatorMethod);
                        break;
                    case IInvocationOperation invocation:
                        CheckCall(context, invocation.TargetMethod);
                        break;
                    case IPropertyReferenceOperation property:
                        CheckProperty(context, property);
                        break;
                }
            }

            private void CheckOperator(OperationAnalysisContext context, string name, IMethodSymbol? method,
                ITypeSymbol? left, ITypeSymbol? right = null)
            {
                if (IsFloating(left) || IsFloating(right))
                    Report(context, Binary32KernelDiagnostics.FloatingOperator, name);
                else if (method != null)
                    CheckCall(context, method);
            }

            private void CheckCall(OperationAnalysisContext context, IMethodSymbol method)
            {
                if (IsPlatformArithmetic(method))
                    Report(context, Binary32KernelDiagnostics.PlatformArithmetic, method.ToDisplayString());
                else if (GetMembership(method) == Membership.Unmarked && !IsAllowedApi(method))
                    Report(context, Binary32KernelDiagnostics.UnmarkedCall, method.ToDisplayString());
            }

            private void CheckProperty(OperationAnalysisContext context, IPropertyReferenceOperation reference)
            {
                var property = reference.Property;
                if (property.DeclaringSyntaxReferences.Length == 0 || IsAutoProperty(property) || IsStorageIndexer(property))
                    return;

                // Reading a property reaches its getter, writing reaches its setter. Updates reach both.
                var write = reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference;
                var update = reference.Parent is ICompoundAssignmentOperation || reference.Parent is IIncrementOrDecrementOperation;
                var getterAllowed = property.GetMethod != null && GetMembership(property.GetMethod) != Membership.Unmarked;
                var setterAllowed = property.SetMethod != null && GetMembership(property.SetMethod) != Membership.Unmarked;
                if (property.ReturnsByRef || property.ReturnsByRefReadonly) write = false;
                if ((update && (!getterAllowed || !setterAllowed)) || (!update && !(write ? setterAllowed : getterAllowed)))
                    Report(context, Binary32KernelDiagnostics.UnmarkedCall, property.ToDisplayString());
            }

            private Membership GetMembership(ISymbol symbol) =>
                _membership.GetOrAdd(symbol.OriginalDefinition, ComputeMembership);

            private static Membership ComputeMembership(ISymbol symbol)
            {
                var kernel = false;
                for (ISymbol? current = symbol; current != null; current = current.ContainingSymbol)
                {
                    if (!(current is IMethodSymbol) && !(current is INamedTypeSymbol)) continue;
                    foreach (var attribute in current.GetAttributes())
                    {
                        var name = attribute.AttributeClass?.Name;
                        if (current is INamedTypeSymbol && name == ArithmeticAttribute) return Membership.Home;
                        if (name == KernelAttribute) kernel = true;
                    }
                }
                return kernel ? Membership.Kernel : Membership.Unmarked;
            }
        }

        private static bool IsFloating(ITypeSymbol? type)
        {
            type = UnwrapNullable(type);
            return type?.SpecialType == SpecialType.System_Single || type?.SpecialType == SpecialType.System_Double
                || type?.SpecialType == SpecialType.System_Decimal;
        }

        private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type) =>
            type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                ? named.TypeArguments[0] : type;

        private static bool IsNumericConversion(IConversionOperation conversion)
        {
            if (conversion.Conversion.IsNumeric) return true;
            if (!conversion.Conversion.IsNullable) return false;
            var source = UnwrapNullable(conversion.Operand.Type)?.SpecialType ?? SpecialType.None;
            var target = UnwrapNullable(conversion.Type)?.SpecialType ?? SpecialType.None;
            // Roslyn classifies lifted numeric conversions as nullable, not numeric. Lifting or
            // unwrapping an unchanged value is storage, so only a change of numeric type counts.
            return source != SpecialType.None && target != SpecialType.None && source != target;
        }

        private static bool IsPlatformArithmetic(IMethodSymbol method)
        {
            var type = method.ContainingType;
            var ns = type.ContainingNamespace.ToDisplayString();
            if (ns == "System")
            {
                if (type.SpecialType == SpecialType.System_Single || type.SpecialType == SpecialType.System_Double
                    || type.Name == "BitConverter") return true;
                if (type.Name == "Math" || type.Name == "MathF") return method.Parameters.Any(p => IsFloating(p.Type));
            }
            return ns == "System.Numerics" || ns.StartsWith("System.Numerics.", StringComparison.Ordinal)
                || ns == "System.Runtime.Intrinsics" || ns.StartsWith("System.Runtime.Intrinsics.", StringComparison.Ordinal);
        }

        private static bool IsAllowedApi(IMethodSymbol target)
        {
            var method = target.ReducedFrom ?? target;
            var type = method.ContainingType;
            if (type.ContainingNamespace.ToDisplayString() != "System") return false;
            if ((type.Name == "Math" || type.Name == "MathF") && !method.Parameters.Any(p => IsFloating(p.Type))) return true;
            if (type.SpecialType == SpecialType.System_Object && method.Name == "ReferenceEquals") return true;
            if (type.Name == "ArgumentNullException" && method.Name == "ThrowIfNull") return true;
            if (type.Name != "Span" && type.Name != "ReadOnlySpan" && type.Name != "MemoryExtensions"
                && type.Name != "Array" && type.Name != "Memory" && type.Name != "ReadOnlyMemory") return false;
            if (method.Name == "op_Implicit" && (type.Name == "Span" || type.Name == "ReadOnlySpan"
                || type.Name == "Memory" || type.Name == "ReadOnlyMemory")) return true;
            switch (method.Name)
            {
                case "AsSpan":
                case "Slice":
                case "CopyTo":
                case "TryCopyTo":
                case "Clear":
                case "Fill":
                case "Overlaps": return true;
                default: return false;
            }
        }

        private static bool IsAutoProperty(IPropertySymbol property)
        {
            // A field-backed computed accessor also has an associated field. Its source body must
            // still be checked, unlike an actual auto-property accessor or positional record property.
            foreach (var reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is PropertyDeclarationSyntax declaration
                    && (declaration.ExpressionBody != null || (declaration.AccessorList?.Accessors.Any(
                        accessor => accessor.Body != null || accessor.ExpressionBody != null) ?? false)))
                    return false;
            }
            return property.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(
                field => SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property));
        }

        private static bool IsStorageIndexer(IPropertySymbol property)
        {
            if (!property.IsIndexer) return false;
            var type = property.ContainingType;
            var ns = type.ContainingNamespace.ToDisplayString();
            return (ns == "System" && (type.Name == "Span" || type.Name == "ReadOnlySpan"))
                || (ns == "System.Collections.Generic" && type.Name == "IReadOnlyList");
        }

        private static void Report(OperationAnalysisContext context, DiagnosticDescriptor rule, params object[] args) =>
            context.ReportDiagnostic(Diagnostic.Create(rule, context.Operation.Syntax.GetLocation(), args));
    }
}
