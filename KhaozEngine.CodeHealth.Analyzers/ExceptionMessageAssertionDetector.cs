using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Finds exception message accesses inside wording assertions.</summary>
    internal static class ExceptionMessageAssertionDetector
    {
        public static IEnumerable<Location> Find(
            SemanticModel model, TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            var root = model.SyntaxTree.GetRoot(cancellationToken);
            foreach (var node in root.DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = node switch
                {
                    MemberAccessExpressionSyntax access => access.Name,
                    MemberBindingExpressionSyntax binding => binding.Name,
                    _ => null,
                };
                if (name?.Identifier.ValueText != "Message" ||
                    model.GetSymbolInfo(node, cancellationToken).Symbol is not IPropertySymbol property ||
                    !IsExceptionMessage(property, symbols.Exception)) continue;

                if (IsWordingAssertionArgument(node, model, symbols, cancellationToken))
                    yield return node.GetLocation();
            }
        }

        private static bool IsExceptionMessage(IPropertySymbol? property, INamedTypeSymbol exception)
        {
            for (; property is not null; property = property.OverriddenProperty)
            {
                if (SymbolEqualityComparer.Default.Equals(property.ContainingType, exception)) return true;
            }
            return false;
        }

        private static bool IsWordingAssertionArgument(SyntaxNode access, SemanticModel model,
            TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            foreach (var ancestor in access.Ancestors())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ancestor is not ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } ||
                    model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method ||
                    !SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.Assert)) continue;

                // The nearest Assert owns this access, including when its method is excluded.
                return method.Name is "Contains" or "Equal" or "NotEqual" or "StartsWith" or
                    "EndsWith" or "Matches" or "True" or "False";
            }
            return false;
        }
    }
}
