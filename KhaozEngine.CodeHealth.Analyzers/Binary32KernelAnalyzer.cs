using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
            Binary32KernelDiagnostics.PlatformArithmetic, Binary32KernelDiagnostics.UnmarkedCall,
            Binary32KernelDiagnostics.UnlistedOperation);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
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
            // Register every kind, including unknown/invalid nodes. The switch below is the only
            // allow-list, so adding a Roslyn operation never silently broadens the inventory.
            context.RegisterOperationAction(state.Analyze,
                ((OperationKind[])Enum.GetValues(typeof(OperationKind))).Distinct().ToArray());
        }

        private enum Membership { Unmarked, Kernel, Home }

        private sealed class CompilationState
        {
            private readonly ConcurrentDictionary<ISymbol, Membership> _membership =
                new ConcurrentDictionary<ISymbol, Membership>(SymbolEqualityComparer.Default);

            public void Analyze(OperationAnalysisContext context)
            {
                // Attribute metadata belongs to the declaration, not its executable kernel body.
                for (var node = context.Operation; node != null; node = node.Parent)
                    if (node is IAttributeOperation) return;
                var owner = context.Operation is ILocalFunctionOperation localOwner ? localOwner.Symbol
                    : context.Operation is IAnonymousFunctionOperation lambdaOwner ? lambdaOwner.Symbol
                    : context.ContainingSymbol;
                // Operation callbacks can name the outer method for a local function or lambda.
                // Its own attribute still counts, even when the outer method is not attributed.
                for (var parent = context.Operation is ILocalFunctionOperation || context.Operation is IAnonymousFunctionOperation
                    ? null : context.Operation.Parent; parent != null; parent = parent.Parent)
                {
                    if (parent is IAnonymousFunctionOperation lambda) { owner = lambda.Symbol; break; }
                    if (parent is ILocalFunctionOperation local) { owner = local.Symbol; break; }
                }
                if (!(owner is IMethodSymbol) || GetMembership(owner) != Membership.Kernel) return;

                var operation = context.Operation;
                if (operation.Type?.TypeKind == TypeKind.Dynamic || IsPointer(operation.Type)
                    || IsUnsafeBody(operation))
                {
                    Reject(context);
                    return;
                }

                switch (operation.Kind)
                {
                    case OperationKind.Literal:
                    case OperationKind.DefaultValue:
                    case OperationKind.LocalReference:
                    case OperationKind.ParameterReference:
                    case OperationKind.FieldReference:
                    case OperationKind.InstanceReference:
                    case OperationKind.ArrayElementReference:
                    case OperationKind.SimpleAssignment:
                    case OperationKind.ObjectCreation:
                    case OperationKind.Block:
                    case OperationKind.MethodBodyOperation:
                    case OperationKind.ConstructorBodyOperation:
                    case OperationKind.Return:
                    case OperationKind.Throw:
                    case OperationKind.VariableDeclarationGroup:
                    case OperationKind.VariableDeclaration:
                    case OperationKind.VariableInitializer:
                    case OperationKind.ExpressionStatement:
                    case OperationKind.Argument:
                    case OperationKind.LocalFunction:
                    case OperationKind.AnonymousFunction:
                    case OperationKind.Empty:
                        return;
                    case OperationKind.VariableDeclarator:
                        var variable = (IVariableDeclaratorOperation)operation;
                        if (variable.Symbol.Type.TypeKind == TypeKind.Dynamic || IsPointer(variable.Symbol.Type))
                            Reject(context);
                        return;
                    case OperationKind.Binary:
                        var binary = (IBinaryOperation)operation;
                        CheckOperator(context, binary.OperatorKind.ToString(), binary.OperatorMethod,
                            binary.LeftOperand.Type, binary.RightOperand.Type);
                        return;
                    case OperationKind.Unary:
                        var unary = (IUnaryOperation)operation;
                        CheckOperator(context, unary.OperatorKind.ToString(), unary.OperatorMethod, unary.Operand.Type);
                        return;
                    case OperationKind.Increment:
                    case OperationKind.Decrement:
                        var increment = (IIncrementOrDecrementOperation)operation;
                        CheckOperator(context, operation.Kind.ToString(), increment.OperatorMethod, increment.Target.Type,
                            integralOnly: true);
                        return;
                    case OperationKind.CompoundAssignment:
                        var compound = (ICompoundAssignmentOperation)operation;
                        CheckOperator(context, compound.OperatorKind.ToString(), compound.OperatorMethod,
                            compound.Target.Type, compound.Value.Type, integralOnly: true);
                        return;
                    case OperationKind.Conversion:
                        CheckConversion(context, (IConversionOperation)operation);
                        return;
                    case OperationKind.Invocation:
                        var invocation = (IInvocationOperation)operation;
                        CheckCall(context, invocation.TargetMethod, invocation.IsVirtual);
                        return;
                    case OperationKind.PropertyReference:
                        CheckProperty(context, (IPropertyReferenceOperation)operation);
                        return;
                    case OperationKind.Conditional:
                        if (((IConditionalOperation)operation).Condition.Type?.SpecialType != SpecialType.System_Boolean)
                            Reject(context);
                        return;
                    case OperationKind.Loop:
                        if (!IsAllowedLoop((ILoopOperation)operation)) Reject(context);
                        return;
                    case OperationKind.Branch:
                        var branch = (IBranchOperation)operation;
                        if (branch.BranchKind != BranchKind.Break && branch.BranchKind != BranchKind.Continue)
                            Reject(context);
                        return;
                    case OperationKind.DelegateCreation:
                        Report(context, Binary32KernelDiagnostics.UnmarkedCall, "delegate creation");
                        return;
                    default:
                        Reject(context);
                        return;
                }
            }

            private void CheckOperator(OperationAnalysisContext context, string name, IMethodSymbol? method,
                ITypeSymbol? left, ITypeSymbol? right = null, bool integralOnly = false)
            {
                if (IsFloating(left) || IsFloating(right))
                    Report(context, Binary32KernelDiagnostics.FloatingOperator, name);
                else if (method != null && IsPlatformArithmetic(method))
                    Report(context, Binary32KernelDiagnostics.PlatformArithmetic, method.ToDisplayString());
                else if (method != null && !IsClosedCall(method))
                    Report(context, Binary32KernelDiagnostics.UnmarkedCall, method.ToDisplayString());
                else if (!IsIntegralOrBool(left, integralOnly) || (right != null && !IsIntegralOrBool(right, integralOnly)))
                    Reject(context);
            }

            private void CheckConversion(OperationAnalysisContext context, IConversionOperation conversion)
            {
                if (conversion.Operand.Type?.TypeKind == TypeKind.Dynamic || IsPointer(conversion.Operand.Type))
                    Reject(context);
                else if (IsNumericConversion(conversion) && !conversion.ConstantValue.HasValue
                    && !conversion.Operand.ConstantValue.HasValue
                    && (IsFloating(conversion.Operand.Type) || IsFloating(conversion.Type)))
                    Report(context, Binary32KernelDiagnostics.FloatingConversion,
                        conversion.Operand.Type?.ToDisplayString() ?? "?", conversion.Type?.ToDisplayString() ?? "?");
                else if (conversion.OperatorMethod != null)
                {
                    var method = conversion.OperatorMethod;
                    if (IsPlatformArithmetic(method) || !IsClosedCall(method) || IsAllowedApi(method))
                        CheckCall(context, method);
                    else
                        Reject(context);
                }
                else if (!IsStorageOrConstantConversion(conversion))
                    Reject(context);
            }

            private void CheckCall(OperationAnalysisContext context, IMethodSymbol method, bool virtualDispatch = false)
            {
                if (IsPlatformArithmetic(method))
                    Report(context, Binary32KernelDiagnostics.PlatformArithmetic, method.ToDisplayString());
                else if (virtualDispatch || !IsClosedCall(method))
                    Report(context, Binary32KernelDiagnostics.UnmarkedCall, method.ToDisplayString());
            }

            private bool IsClosedCall(IMethodSymbol method) =>
                !method.IsVirtual && !method.IsAbstract && !method.IsOverride
                && method.ContainingType?.TypeKind != TypeKind.Interface
                && method.MethodKind != MethodKind.DelegateInvoke
                && (GetMembership(method) != Membership.Unmarked || IsAllowedApi(method));

            private void CheckProperty(OperationAnalysisContext context, IPropertyReferenceOperation reference)
            {
                var property = reference.Property;
                // Storage indexers are the deliberate interface-dispatch exception in the brief.
                if (IsStorageIndexer(property)) return;
                if (property.IsVirtual || property.IsAbstract || property.IsOverride
                    || property.ContainingType.TypeKind == TypeKind.Interface)
                {
                    Report(context, Binary32KernelDiagnostics.UnmarkedCall, property.ToDisplayString());
                    return;
                }
                if (IsAutoProperty(property)) return;

                // Ref-return writes mutate the storage supplied by the getter, never a setter.
                var write = reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference;
                var update = reference.Parent is ICompoundAssignmentOperation || reference.Parent is IIncrementOrDecrementOperation;
                if (property.ReturnsByRef || property.ReturnsByRefReadonly) { write = false; update = false; }
                var getterAllowed = property.GetMethod != null && IsClosedCall(property.GetMethod);
                var setterAllowed = property.SetMethod != null && IsClosedCall(property.SetMethod);
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
            if (conversion.Conversion.IsNumeric || UnwrapNullable(conversion.Operand.Type)?.TypeKind == TypeKind.Enum
                || UnwrapNullable(conversion.Type)?.TypeKind == TypeKind.Enum) return true;
            if (!conversion.Conversion.IsNullable) return false;
            var source = UnwrapNullable(conversion.Operand.Type)?.SpecialType ?? SpecialType.None;
            var target = UnwrapNullable(conversion.Type)?.SpecialType ?? SpecialType.None;
            // Roslyn classifies lifted numeric conversions as nullable, not numeric. Lifting or
            // unwrapping an unchanged value is storage, so only a change of numeric type counts.
            return source != SpecialType.None && target != SpecialType.None && source != target;
        }

        private static bool IsIntegralOrBool(ITypeSymbol? type, bool integralOnly = false)
        {
            type = UnwrapNullable(type);
            if (type?.TypeKind == TypeKind.Enum) return true;
            switch (type?.SpecialType)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                case SpecialType.System_Char: return true;
                case SpecialType.System_Boolean: return !integralOnly;
                default: return false;
            }
        }

        private static bool IsStorageOrConstantConversion(IConversionOperation conversion)
        {
            var source = UnwrapNullable(conversion.Operand.Type);
            var target = UnwrapNullable(conversion.Type);
            // Existing inventory exceptions preserve storage without performing numeric work.
            var csharp = conversion.GetConversion();
            return conversion.Conversion.IsIdentity || conversion.ConstantValue.HasValue
                || (conversion.Operand.ConstantValue.HasValue && IsNumericConversion(conversion))
                || (IsIntegralOrBool(source, true) && IsIntegralOrBool(target, true))
                || csharp.IsReference || csharp.IsBoxing || csharp.IsUnboxing
                || (csharp.IsNullable && SymbolEqualityComparer.Default.Equals(source, target));
        }

        private static bool IsAllowedLoop(ILoopOperation loop)
        {
            if (loop is IForLoopOperation || loop is IWhileLoopOperation) return true;
            if (!(loop is IForEachLoopOperation each) || each.IsAsynchronous
                || !(each.Syntax is CommonForEachStatementSyntax syntax)) return false;
            var collection = each.Collection;
            while (collection is IConversionOperation conversion && conversion.IsImplicit)
                collection = conversion.Operand;
            var type = collection.Type;
            var storage = type is IArrayTypeSymbol || (type is INamedTypeSymbol named
                && named.ContainingNamespace.ToDisplayString() == "System"
                && (named.Name == "Span" || named.Name == "ReadOnlySpan"));
            return storage && (each.SemanticModel?.GetForEachStatementInfo(syntax).ElementConversion.IsIdentity ?? false);
        }

        private static bool IsPointer(ITypeSymbol? type) =>
            type?.TypeKind == TypeKind.Pointer || type?.TypeKind == TypeKind.FunctionPointer;

        private static bool IsUnsafeBody(IOperation operation)
        {
            if (operation is IBlockOperation && operation.Syntax.Parent is UnsafeStatementSyntax) return true;
            if (!(operation is IMethodBodyOperation) && !(operation is IConstructorBodyOperation)
                && !(operation is ILocalFunctionOperation)) return false;
            return operation.Syntax.AncestorsAndSelf().Any(node =>
                (node is MemberDeclarationSyntax member && member.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                || (node is LocalFunctionStatementSyntax local && local.Modifiers.Any(SyntaxKind.UnsafeKeyword)));
        }

        private static void Reject(OperationAnalysisContext context) =>
            Report(context, Binary32KernelDiagnostics.UnlistedOperation, context.Operation.Kind.ToString());

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
