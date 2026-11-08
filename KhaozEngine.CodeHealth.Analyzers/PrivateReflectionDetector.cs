using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Finds non-public reflection flags and unsafe accessor attribute applications.</summary>
    internal static class PrivateReflectionDetector
    {
        public static IEnumerable<Location> Find(
            SemanticModel model, TestHealthSymbols symbols, CancellationToken cancellationToken)
        {
            var root = model.SyntaxTree.GetRoot(cancellationToken);
            foreach (var node in root.DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "NonPublic" &&
                    symbols.BindingFlags is not null &&
                    model.GetSymbolInfo(identifier, cancellationToken).Symbol is IFieldSymbol field &&
                    SymbolEqualityComparer.Default.Equals(field.ContainingType, symbols.BindingFlags))
                {
                    yield return identifier.GetLocation();
                }
                else if (node is AttributeSyntax attribute && symbols.UnsafeAccessorAttribute is not null &&
                    model.GetSymbolInfo(attribute, cancellationToken).Symbol is IMethodSymbol constructor &&
                    SymbolEqualityComparer.Default.Equals(constructor.ContainingType, symbols.UnsafeAccessorAttribute))
                {
                    yield return attribute.GetLocation();
                }
            }
        }
    }
}
