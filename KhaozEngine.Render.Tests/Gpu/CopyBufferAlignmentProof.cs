using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Narrow source proofs for offsets that are validated at runtime or converted from uint words.</summary>
internal static class CopyBufferAlignmentProof
{
    static readonly MetadataReference CoreLibrary = MetadataReference.CreateFromFile(typeof(uint).Assembly.Location);

    internal static bool Accepts(string expression, string source, int copyIndex, string helper)
    {
        if (Regex.IsMatch(expression, @"^CoverageGpuLayout\.Bytes\([^;]+\)$")
            && BalancedArgument(expression))
            return !helper.Contains('#', StringComparison.Ordinal)
                && Regex.Matches(helper, @"\b[A-Za-z_][\w<>?,.\[\]]*\s+Bytes\s*(?:<[^<>]*>)?\s*\(").Count == 1
                && Regex.Matches(helper, @"\buint\s+Bytes\s*\(\s*uint\s+words\s*\)\s*=>\s*"
                + @"checked\s*\(\s*words\s*\*\s*sizeof\s*\(\s*uint\s*\)\s*\)\s*;").Count == 1;
        return RuntimeValidationProves(expression, source, copyIndex);
    }

    static bool RuntimeValidationProves(string expression, string source, int copyIndex)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        InvocationExpressionSyntax? copy = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(call => call.SpanStart == copyIndex
                && call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CopyBuffer" });
        if (copy is null || copy.ArgumentList.Arguments.Count != 5) return false;
        ExpressionSyntax? argument = copy.ArgumentList.Arguments.Where((_, index) => index is 1 or 3)
            .Select(value => value.Expression).FirstOrDefault(value => value.ToString() == expression);
        if (argument is not IdentifierNameSyntax identifier) return false;

        StatementSyntax? statement = copy.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        SyntaxNode? owner = copy.Ancestors().FirstOrDefault(node => node is BaseMethodDeclarationSyntax
            or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        if (statement?.Parent is not BlockSyntax block || owner is not MethodDeclarationSyntax { Body: { } body }
            || body.ContainsDiagnostics || body.ContainsDirectives
            || body.DescendantNodes().Any(node => node is GotoStatementSyntax)) return false;

        var compilation = CSharpCompilation.Create("CopyBufferAlignmentProof", [tree], [CoreLibrary],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        SemanticModel model = compilation.GetSemanticModel(tree);
        ISymbol? symbol = model.GetSymbolInfo(identifier).Symbol;
        if (symbol is not (ILocalSymbol { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_UInt32 }
            or IParameterSymbol { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_UInt32 })) return false;
        // Only locals initialized at their declaration qualify for this narrow proof.
        if (symbol is ILocalSymbol local && !local.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null })) return false;

        DataFlowAnalysis? whole = model.AnalyzeDataFlow(body);
        if (whole is not { Succeeded: true } || whole.Captured.Contains(symbol, SymbolEqualityComparer.Default)
            || whole.UnsafeAddressTaken.Contains(symbol, SymbolEqualityComparer.Default)) return false;

        int copyStatement = block.Statements.IndexOf(statement);
        for (int index = copyStatement - 1; index >= 0; index--)
        {
            if (block.Statements[index] is not ExpressionStatementSyntax
                { Expression: InvocationExpressionSyntax validation }
                || validation.Expression is not MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "GpuCopyAlignment" },
                    Name.Identifier.ValueText: "RequireAlignedOffset"
                }
                || validation.ArgumentList.Arguments.Count == 0
                || !SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(validation.ArgumentList.Arguments[0].Expression).Symbol, symbol)) continue;

            if (index + 1 == copyStatement) return true;
            DataFlowAnalysis? between = model.AnalyzeDataFlow(block.Statements[index + 1], block.Statements[copyStatement - 1]);
            if (between is { Succeeded: true } && !between.WrittenInside.Contains(symbol, SymbolEqualityComparer.Default))
                return true;
        }
        return false;
    }

    static bool BalancedArgument(string expression)
    {
        int open = expression.IndexOf('(', StringComparison.Ordinal);
        return Depth(expression[(open + 1)..^1], '(', ')') == 0;
    }

    static int Depth(string text, char open = '{', char close = '}')
    {
        int depth = 0;
        foreach (char c in text)
        {
            if (c == open) depth++;
            else if (c == close && --depth < 0) return -1;
        }
        return depth;
    }
}
