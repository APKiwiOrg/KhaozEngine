using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Finds tests whose direct assertions can all be skipped by empty iteration.</summary>
    internal static class LoopOnlyAssertionDetector
    {
        public static IEnumerable<Location> Find(
            SemanticModel model, TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            var root = model.SyntaxTree.GetRoot(cancellationToken);
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTestMethod(method, model, symbols, cancellationToken)) continue;

                SyntaxNode? body = method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression;
                if (body is null) continue;

                var hasAssertion = false;
                var allGuarded = true;
                foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (GetAssertMethod(invocation, model, symbols, cancellationToken) is null) continue;

                    hasAssertion = true;
                    if (IsGuarded(invocation, method, model, symbols, cancellationToken)) continue;
                    allGuarded = false;
                    break;
                }
                if (hasAssertion && allGuarded) yield return method.Identifier.GetLocation();
            }
        }

        private static bool IsTestMethod(MethodDeclarationSyntax method, SemanticModel model,
            TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            if (model.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol symbol) return false;
            foreach (var attribute in symbol.GetAttributes())
            {
                for (var type = attribute.AttributeClass; type is not null; type = type.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(type, symbols.FactAttribute)) return true;
                }
            }
            return false;
        }

        private static IMethodSymbol? GetAssertMethod(InvocationExpressionSyntax invocation, SemanticModel model,
            TestHealthSymbols symbols, CancellationToken cancellationToken) =>
            model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
            SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.Assert) ? method : null;

        private static bool IsGuarded(InvocationExpressionSyntax invocation, MethodDeclarationSyntax method,
            SemanticModel model, TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            if (IsPossiblyEmptyAll(invocation, model, symbols, cancellationToken)) return true;

            foreach (var ancestor in invocation.Ancestors())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A local function owns its assertions independently of the caller's loops.
                if (ancestor == method || ancestor is LocalFunctionStatementSyntax) break;
                if (ancestor is CommonForEachStatementSyntax each &&
                    each.Statement.Span.Contains(invocation.Span) &&
                    !IsNonEmpty(each.Expression, model, symbols, cancellationToken)) return true;
                if (ancestor is ForStatementSyntax loop &&
                    (loop.Statement.Span.Contains(invocation.Span) ||
                     loop.Incrementors.Any(increment => increment.Span.Contains(invocation.Span))) &&
                    !HasFirstIteration(loop, model, cancellationToken)) return true;
                if (ancestor is WhileStatementSyntax conditionLoop &&
                    conditionLoop.Statement.Span.Contains(invocation.Span)) return true;

                if (ancestor is not AnonymousFunctionExpressionSyntax lambda) continue;
                SyntaxNode argumentExpression = lambda;
                while (argumentExpression.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
                    argumentExpression = argumentExpression.Parent;
                if (argumentExpression.Parent is ArgumentSyntax
                    { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax owner } } &&
                    IsPossiblyEmptyAll(owner, model, symbols, cancellationToken)) return true;
            }
            return false;
        }

        private static bool IsPossiblyEmptyAll(InvocationExpressionSyntax invocation, SemanticModel model,
            TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            var method = GetAssertMethod(invocation, model, symbols, cancellationToken);
            if (method?.Name != "All") return false;
            var source = ArgumentFor(invocation, method, 0);
            return source is null || !IsNonEmpty(source, model, symbols, cancellationToken);
        }

        private static ExpressionSyntax? ArgumentFor(InvocationExpressionSyntax invocation,
            IMethodSymbol method, int parameterIndex)
        {
            var arguments = invocation.ArgumentList.Arguments;
            foreach (var argument in arguments)
            {
                if (argument.NameColon?.Name.Identifier.ValueText == method.Parameters[parameterIndex].Name)
                    return argument.Expression;
            }
            return parameterIndex < arguments.Count && arguments[parameterIndex].NameColon is null
                ? arguments[parameterIndex].Expression : null;
        }

        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (true)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        break;
                    default:
                        return expression;
                }
            }
        }

        private static bool IsNonEmpty(ExpressionSyntax expression, SemanticModel model,
            TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            expression = Unwrap(expression);
            switch (expression)
            {
                case CastExpressionSyntax cast:
                    return IsNonEmpty(cast.Expression, model, symbols, cancellationToken);
                case ImplicitArrayCreationExpressionSyntax array:
                    return array.Initializer.Expressions.Count > 0;
                case ArrayCreationExpressionSyntax array:
                    if (array.Initializer is not null) return array.Initializer.Expressions.Count > 0;
                    return array.Type.RankSpecifiers[0].Sizes.All(size =>
                        model.GetConstantValue(size, cancellationToken) is { HasValue: true, Value: { } count } &&
                        IsPositiveIntegral(count));
                case CollectionExpressionSyntax collection:
                    return collection.Elements.Any(element => element is ExpressionElementSyntax);
                case ObjectCreationExpressionSyntax creation:
                    return HasCollectionElements(creation.Initializer);
                case ImplicitObjectCreationExpressionSyntax creation:
                    return HasCollectionElements(creation.Initializer);
                case InvocationExpressionSyntax invocation:
                    if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
                        return false;
                    if (method.Name == "GetValues" && symbols.Enum is not null &&
                        SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.Enum)) return true;
                    if (method.Name != "Range" || symbols.Enumerable is null ||
                        !SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.Enumerable)) return false;
                    var countExpression = ArgumentFor(invocation, method, 1);
                    return countExpression is not null &&
                        model.GetConstantValue(countExpression, cancellationToken) is
                            { HasValue: true, Value: int rangeCount } &&
                        rangeCount > 0;
                default:
                    return false;
            }
        }

        private static bool HasCollectionElements(InitializerExpressionSyntax? initializer) =>
            initializer is not null && initializer.IsKind(SyntaxKind.CollectionInitializerExpression) &&
            initializer.Expressions.Count > 0;

        // Every integral type a constant array dimension can legally have.
        private static bool IsPositiveIntegral(object value) => value switch
        {
            sbyte number => number > 0,
            byte number => number > 0,
            short number => number > 0,
            ushort number => number > 0,
            char number => number > 0,
            int number => number > 0,
            uint number => number > 0,
            long number => number > 0,
            ulong number => number > 0,
            _ => false,
        };

        private static bool HasFirstIteration(ForStatementSyntax loop, SemanticModel model,
            CancellationToken cancellationToken)
        {
            if (loop.Condition is null || Unwrap(loop.Condition) is not BinaryExpressionSyntax condition ||
                model.GetOperation(condition, cancellationToken) is not IBinaryOperation operation) return false;

            var leftSymbol = model.GetSymbolInfo(Unwrap(condition.Left), cancellationToken).Symbol;
            var rightSymbol = model.GetSymbolInfo(Unwrap(condition.Right), cancellationToken).Symbol;
            if (!TryFindCounterStart(loop, model, leftSymbol, rightSymbol, cancellationToken,
                    out var counter, out var initialValue) ||
                !TryLiteralNumber(initialValue, model, cancellationToken, out var start)) return false;

            // Honor rounding on assignment before applying the comparison's numeric promotion.
            switch (model.GetTypeInfo(initialValue, cancellationToken).ConvertedType?.SpecialType)
            {
                case SpecialType.System_Single: start = Convert.ToSingle(start); break;
                case SpecialType.System_Double: start = Convert.ToDouble(start); break;
            }

            object left;
            object right;
            if (SymbolEqualityComparer.Default.Equals(leftSymbol, counter) &&
                TryLiteralNumber(condition.Right, model, cancellationToken, out right)) left = start;
            else if (SymbolEqualityComparer.Default.Equals(rightSymbol, counter) &&
                     TryLiteralNumber(condition.Left, model, cancellationToken, out left)) right = start;
            else return false;

            int? comparison = operation.LeftOperand.Type?.SpecialType switch
            {
                SpecialType.System_Single => Convert.ToSingle(left).CompareTo(Convert.ToSingle(right)),
                SpecialType.System_Double => Convert.ToDouble(left).CompareTo(Convert.ToDouble(right)),
                SpecialType.System_Decimal or SpecialType.System_Int32 or SpecialType.System_UInt32 or
                    SpecialType.System_Int64 or SpecialType.System_UInt64 or
                    SpecialType.System_IntPtr or SpecialType.System_UIntPtr =>
                    Convert.ToDecimal(left).CompareTo(Convert.ToDecimal(right)),
                _ => null,
            };
            if (comparison is null) return false;

            return condition.Kind() switch
            {
                SyntaxKind.LessThanExpression => comparison < 0,
                SyntaxKind.LessThanOrEqualExpression => comparison <= 0,
                SyntaxKind.GreaterThanExpression => comparison > 0,
                SyntaxKind.GreaterThanOrEqualExpression => comparison >= 0,
                SyntaxKind.EqualsExpression => comparison == 0,
                SyntaxKind.NotEqualsExpression => comparison != 0,
                _ => false,
            };
        }

        // The counter is whichever for-initializer variable the condition compares, so a header that
        // declares or assigns several variables still proves its first iteration.
        private static bool TryFindCounterStart(ForStatementSyntax loop, SemanticModel model, ISymbol? leftSymbol,
            ISymbol? rightSymbol, CancellationToken cancellationToken, out ISymbol counter,
            out ExpressionSyntax initialValue)
        {
            bool IsCompared(ISymbol? candidate) =>
                candidate is not null && (SymbolEqualityComparer.Default.Equals(candidate, leftSymbol) ||
                                          SymbolEqualityComparer.Default.Equals(candidate, rightSymbol));

            if (loop.Declaration is not null)
            {
                foreach (var variable in loop.Declaration.Variables)
                {
                    var declared = model.GetDeclaredSymbol(variable, cancellationToken);
                    if (IsCompared(declared) && variable.Initializer is not null &&
                        !OtherInitializersMention(loop, variable, declared!.Name))
                    {
                        counter = declared!;
                        initialValue = variable.Initializer.Value;
                        return true;
                    }
                }
            }
            foreach (var initializer in loop.Initializers)
            {
                if (initializer is not AssignmentExpressionSyntax assignment ||
                    !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) continue;
                var assigned = model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
                if (IsCompared(assigned) && !OtherInitializersMention(loop, assignment, assigned!.Name))
                {
                    counter = assigned!;
                    initialValue = assignment.Right;
                    return true;
                }
            }
            counter = null!;
            initialValue = null!;
            return false;
        }

        // Another initializer that names the counter could overwrite its start value, so the header
        // proves nothing then. A name match is deliberately conservative.
        private static bool OtherInitializersMention(ForStatementSyntax loop, SyntaxNode counterInitializer,
            string name)
        {
            IEnumerable<SyntaxNode> parts = loop.Declaration is null
                ? loop.Initializers
                : loop.Declaration.Variables.Cast<SyntaxNode>().Concat(loop.Initializers);
            return parts.Where(part => part != counterInitializer).Any(part =>
                part.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Any(identifier => identifier.Identifier.ValueText == name));
        }

        private static bool TryLiteralNumber(ExpressionSyntax expression, SemanticModel model,
            CancellationToken cancellationToken, out object number)
        {
            expression = Unwrap(expression);
            var literal = expression;
            if (literal is PrefixUnaryExpressionSyntax unary &&
                (unary.IsKind(SyntaxKind.UnaryMinusExpression) || unary.IsKind(SyntaxKind.UnaryPlusExpression)))
                literal = Unwrap(unary.Operand);
            if ((literal.IsKind(SyntaxKind.NumericLiteralExpression) ||
                 literal.IsKind(SyntaxKind.CharacterLiteralExpression)) &&
                model.GetConstantValue(expression, cancellationToken) is { HasValue: true, Value: { } value })
            {
                // A char compares through its code point. Convert.ToDecimal refuses a char.
                number = value is char character ? (int)character : value;
                return true;
            }
            number = 0;
            return false;
        }
    }
}
