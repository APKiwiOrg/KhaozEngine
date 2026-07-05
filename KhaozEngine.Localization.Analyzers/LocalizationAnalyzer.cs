using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.Localization.Analyzers;

/// <summary>
/// Enforces the KhaozEngine LocalizedText contract. KELOC001: a call to a method/constructor marked
/// <c>[LocalizationStringSink]</c> (the obsolete raw-string Gui overloads, or a consumer-marked sink).
/// KELOC002: <c>LocalizedText.Raw(...)</c> used outside a <c>[LocalizationExempt]</c> scope and outside
/// DEBUG-conditional code. KELOC003: a bare string literal drawn straight to the engine's 2D text primitive
/// <c>Render2D.SpriteBatch.DrawString</c> (the sink games hit when they render UI without Gui widgets).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LocalizationAnalyzer : DiagnosticAnalyzer
{
    private const string StringSinkAttr = "KhaozEngine.App.LocalizationStringSinkAttribute";
    private const string ExemptAttr = "KhaozEngine.App.LocalizationExemptAttribute";
    private const string ConditionalAttr = "System.Diagnostics.ConditionalAttribute";
    private const string LocalizedTextType = "KhaozEngine.App.LocalizedText";
    private const string SpriteBatchType = "KhaozEngine.Render2D.SpriteBatch";
    private const string DrawStringMethod = "DrawString";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            LocalizationDiagnostics.RawStringSink,
            LocalizationDiagnostics.RawOutsideExempt,
            LocalizationDiagnostics.RawDrawString);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext ctx)
    {
        var invocation = (IInvocationOperation)ctx.Operation;
        IMethodSymbol target = invocation.TargetMethod;

        if (HasAttribute(target, StringSinkAttr))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawStringSink, invocation.Syntax.GetLocation(), target.Name));
            return;
        }

        if (target.Name == DrawStringMethod &&
            target.ContainingType?.ToDisplayString() == SpriteBatchType)
        {
            AnalyzeDrawStringText(ctx, invocation);
            return;
        }

        if (target.Name == "Raw" &&
            target.ContainingType?.ToDisplayString() == LocalizedTextType &&
            !IsExempt(ctx.ContainingSymbol) &&
            !IsInsideActiveDebugRegion(invocation.Syntax))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawOutsideExempt, invocation.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext ctx)
    {
        var creation = (IObjectCreationOperation)ctx.Operation;
        if (creation.Constructor is { } ctor && HasAttribute(ctor, StringSinkAttr))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawStringSink, creation.Syntax.GetLocation(), ctor.ContainingType.Name));
        }
    }

    // KELOC003: a bare string literal drawn straight to the engine's 2D text primitive SpriteBatch.DrawString.
    // v1 catches only non-interpolated, non-verbatim string LITERALS - variables, interpolation, and concatenation
    // are dynamic and localized at their source, so they stay out of scope. Single-character tokens (a close 'X'),
    // letter-free tokens (numbers / format like "{0}"), and [LocalizationExempt] / DEBUG scopes are all allowed, so
    // DrawString's constant use for numbers, glyphs, names, and debug output does not become a false positive.
    private static void AnalyzeDrawStringText(OperationAnalysisContext ctx, IInvocationOperation invocation)
    {
        // The display text is the single string-typed parameter ('text' on both overloads).
        IArgumentOperation? textArg = null;
        foreach (IArgumentOperation arg in invocation.Arguments)
        {
            if (arg.Parameter?.Type.SpecialType == SpecialType.System_String) { textArg = arg; break; }
        }
        if (textArg is null) return;

        IOperation value = textArg.Value;
        if (value is IConversionOperation conv) value = conv.Operand;
        if (value is not ILiteralOperation lit) return;          // variable / interpolation / concat -> out of scope
        if (lit.ConstantValue.Value is not string s) return;
        if (!IsPlainStringLiteral(lit)) return;                  // verbatim @"..." / raw """...""" -> out of scope
        if (s.Length <= 1) return;                               // single-glyph tokens allowed
        if (!s.Any(char.IsLetter)) return;                       // numbers / format tokens allowed

        if (IsExempt(ctx.ContainingSymbol)) return;
        if (IsInsideActiveDebugRegion(invocation.Syntax)) return;

        ctx.ReportDiagnostic(Diagnostic.Create(
            LocalizationDiagnostics.RawDrawString, lit.Syntax.GetLocation()));
    }

    // A plain double-quoted string literal, i.e. NOT verbatim (@"...") and NOT a raw/utf8 string literal. Verbatim
    // and raw literals carry a distinct token text/kind and are deliberately out of the v1 scope.
    private static bool IsPlainStringLiteral(ILiteralOperation lit)
    {
        if (lit.Syntax is not LiteralExpressionSyntax les) return false;
        if (!les.Token.IsKind(SyntaxKind.StringLiteralToken)) return false;
        return !les.Token.Text.StartsWith("@", System.StringComparison.Ordinal);
    }

    private static bool HasAttribute(ISymbol symbol, string fullName)
    {
        foreach (AttributeData a in symbol.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == fullName) return true;
        }
        return false;
    }

    // Walk method -> containing type(s) for [LocalizationExempt] or [Conditional("DEBUG")], then the assembly.
    private static bool IsExempt(ISymbol? symbol)
    {
        for (ISymbol? s = symbol; s is not null and not INamespaceSymbol; s = s.ContainingSymbol)
        {
            if (HasAttribute(s, ExemptAttr)) return true;
            if (IsConditionalDebug(s)) return true;
        }
        return symbol?.ContainingAssembly is { } asm && HasAttribute(asm, ExemptAttr);
    }

    private static bool IsConditionalDebug(ISymbol s)
    {
        foreach (AttributeData a in s.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == ConditionalAttr &&
                a.ConstructorArguments.Length == 1 &&
                a.ConstructorArguments[0].Value as string == "DEBUG")
            {
                return true;
            }
        }
        return false;
    }

    // True when the node sits lexically inside an active `#if DEBUG` (condition mentioning DEBUG) branch. Under a
    // non-DEBUG build the branch is inactive and never parsed into the tree, so this only matters in DEBUG builds.
    private static bool IsInsideActiveDebugRegion(SyntaxNode node)
    {
        SyntaxNode root = node.SyntaxTree.GetRoot();
        foreach (var ifDir in root.DescendantNodes(descendIntoTrivia: true).OfType<IfDirectiveTriviaSyntax>())
        {
            if (!ifDir.BranchTaken) continue;
            if (!ifDir.Condition.ToString().Contains("DEBUG")) continue;

            var related = ifDir.GetRelatedDirectives();
            int idx = related.IndexOf(ifDir);
            if (idx < 0 || idx + 1 >= related.Count) continue;

            // The taken branch runs from just after the #if to the next related directive (#elif/#else/#endif).
            DirectiveTriviaSyntax next = related[idx + 1];
            if (node.SpanStart >= ifDir.Span.End && node.Span.End <= next.SpanStart) return true;
        }
        return false;
    }
}
