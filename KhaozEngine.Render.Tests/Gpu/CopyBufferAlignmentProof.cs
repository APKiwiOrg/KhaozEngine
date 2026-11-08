using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Narrow proofs for runtime-validated offsets and explicit unsigned word conversions.</summary>
internal static class CopyBufferAlignmentProof
{
    static readonly MetadataReference[] References = new[]
        {
            typeof(uint).Assembly, typeof(Unsafe).Assembly, Assembly.Load("System.Runtime"),
            typeof(KhaozEngine.Gpu.IGpuCommandList).Assembly
        }.Distinct().Select(assembly => MetadataReference.CreateFromFile(assembly.Location)).ToArray();

    internal static CSharpCompilation CreateCompilation(SyntaxTree tree)
        => CSharpCompilation.Create("KhaozEngine.Render.Tests", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    internal static bool Accepts(string expression, string source, int copyIndex)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        InvocationExpressionSyntax? copy = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(call => call.SpanStart == copyIndex
                && call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CopyBuffer" });
        if (copy is null || copy.ArgumentList.Arguments.Count != 5) return false;
        ExpressionSyntax? argument = copy.ArgumentList.Arguments.Where((_, index) => index is 1 or 3)
            .Select(value => value.Expression).FirstOrDefault(value => value.ToString() == expression);
        if (argument is not null && IsUnsignedWordConversion(argument)) return true;
        if (argument is not IdentifierNameSyntax identifier) return false;

        StatementSyntax? statement = copy.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        SyntaxNode? owner = copy.Ancestors().FirstOrDefault(node => node is BaseMethodDeclarationSyntax
            or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        if (statement?.Parent is not BlockSyntax block || owner is not MethodDeclarationSyntax { Body: { } body }
            || body.ContainsDiagnostics || body.ContainsDirectives
            || body.DescendantNodes().Any(node => node is GotoStatementSyntax)) return false;

        CSharpCompilation compilation = CreateCompilation(tree);
        SemanticModel model = compilation.GetSemanticModel(tree);
        ISymbol? symbol = model.GetSymbolInfo(identifier).Symbol;
        if (symbol is not (ILocalSymbol { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_UInt32 }
            or IParameterSymbol { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_UInt32 })) return false;
        // Only locals initialized at their declaration qualify for this narrow proof.
        if (symbol is ILocalSymbol local && !local.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null })) return false;

        if (model.GetDiagnostics(owner.Span).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            || model.GetOperation(body) is not IBlockOperation operation
            || model.GetDeclaredSymbol(owner) is not IMethodSymbol method) return false;
        foreach (IOperation use in operation.DescendantsAndSelf())
        {
            if (use is IInvalidOperation) return false;
            ISymbol? referenced = use switch
            {
                ILocalReferenceOperation reference => reference.Local,
                IParameterReferenceOperation reference => reference.Parameter,
                _ => null
            };
            if (SymbolEqualityComparer.Default.Equals(referenced, symbol) && !AllowedReference(use, method))
                return false;
        }

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

            DataFlowAnalysis? range = model.AnalyzeDataFlow(block.Statements[index], statement);
            if (range is { Succeeded: true } && !range.WrittenInside.Contains(symbol, SymbolEqualityComparer.Default))
                return true;
        }
        return false;
    }

    static bool IsUnsignedWordConversion(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);
        // Both operands are uint, so successful multiplication by a multiple of four has two zero low bits.
        // Explicit casts keep this proof independent of unresolved types elsewhere in the source file.
        return expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.MultiplyExpression)
            && Unwrap(binary.Left) is CastExpressionSyntax { Type: PredefinedTypeSyntax { Keyword.ValueText: "uint" } }
            && Unwrap(binary.Right) is LiteralExpressionSyntax literal && literal.Token.Value is uint multiple
            && multiple % 4u == 0;
    }

    static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            if (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            else if (expression is CheckedExpressionSyntax checkedExpression) expression = checkedExpression.Expression;
            else return expression;
        }
    }

    static bool AllowedReference(IOperation reference, IMethodSymbol method)
    {
        // nameof binds symbols but never evaluates its argument or exposes their storage.
        for (IOperation? ancestor = reference.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor is INameOfOperation nameOf
                && nameOf.Argument.Syntax.Span.Contains(reference.Syntax.Span)) return true;

        return reference.Parent switch
        {
            IArgumentOperation argument => argument.Value == reference && argument.Parameter?.RefKind == RefKind.None,
            IBinaryOperation binary => binary.LeftOperand == reference || binary.RightOperand == reference,
            IUnaryOperation unary => unary.Operand == reference,
            IConversionOperation conversion => conversion.Operand == reference,
            IInterpolationOperation interpolation => interpolation.Expression == reference || interpolation.Alignment == reference,
            IVariableInitializerOperation initializer => initializer.Value == reference
                && initializer.Parent is IVariableDeclaratorOperation { Symbol.RefKind: RefKind.None },
            IReturnOperation returned => returned.ReturnedValue == reference && !method.ReturnsByRef && !method.ReturnsByRefReadonly,
            ISimpleAssignmentOperation { IsRef: false } assignment => assignment.Value == reference || assignment.Target == reference,
            ICompoundAssignmentOperation assignment => assignment.Value == reference || assignment.Target == reference,
            IIncrementOrDecrementOperation increment => increment.Target == reference,
            _ => false
        };
    }

}
