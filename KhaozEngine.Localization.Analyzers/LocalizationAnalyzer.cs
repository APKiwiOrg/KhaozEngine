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

    // KELOC003: player-facing text drawn straight to the engine's 2D text primitive SpriteBatch.DrawString.
    // Scans plain string LITERALS, plus the literal segments of interpolated ($"...") and concatenated ("a" + b)
    // strings. The interpolation holes ({expr}) and non-constant concat operands are dynamic and localized at
    // their source, so they stay out of scope. Single-character tokens (a close 'X'), letter-free tokens (numbers
    // / format like "{0}"), verbatim/raw literals, and [LocalizationExempt] / DEBUG scopes are all allowed, so
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

        // Exempt / DEBUG scoping is a property of the whole call site, so gate once before scanning segments.
        if (IsExempt(ctx.ContainingSymbol)) return;
        if (IsInsideActiveDebugRegion(invocation.Syntax)) return;

        ScanTextOperand(ctx, textArg.Value);
    }

    // Report every hardcoded, player-facing string literal reachable from a DrawString text argument: a bare
    // literal, a text segment of an interpolated string, or a literal operand of a string concatenation.
    private static void ScanTextOperand(OperationAnalysisContext ctx, IOperation value)
    {
        if (value is IConversionOperation conv) value = conv.Operand;

        switch (value)
        {
            case ILiteralOperation lit:
                CheckPlainLiteral(ctx, lit);
                break;
            case IInterpolatedStringOperation interp:
                ScanInterpolatedString(ctx, interp);
                break;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } bin:
                ScanTextOperand(ctx, bin.LeftOperand);
                ScanTextOperand(ctx, bin.RightOperand);
                break;
        }
    }

    // A bare double-quoted string literal ("Play"). Verbatim @"..." / raw """...""" literals stay out of scope.
    private static void CheckPlainLiteral(OperationAnalysisContext ctx, ILiteralOperation lit)
    {
        if (lit.ConstantValue.Value is not string s) return;
        if (!IsPlainStringLiteral(lit)) return;                  // verbatim @"..." / raw """...""" -> out of scope
        if (!IsLocalizableText(s)) return;                       // single glyphs and letter-free tokens allowed
        ctx.ReportDiagnostic(Diagnostic.Create(
            LocalizationDiagnostics.RawDrawString, lit.Syntax.GetLocation()));
    }

    // The literal text segments of an interpolated string ($"Score: {n}" -> "Score: "). The interpolation holes
    // ({n}) are dynamic and stay out of scope. Verbatim ($@"...") and raw ($"""...""") interpolated strings are
    // skipped whole, matching the verbatim/raw carve-out for plain literals.
    private static void ScanInterpolatedString(OperationAnalysisContext ctx, IInterpolatedStringOperation interp)
    {
        if (interp.Syntax is not InterpolatedStringExpressionSyntax ise) return;
        if (!ise.StringStartToken.IsKind(SyntaxKind.InterpolatedStringStartToken)) return;

        foreach (IOperation part in interp.Parts)
        {
            if (part is not IInterpolatedStringTextOperation textPart) continue;   // skip {expr} holes
            if (textPart.Text.ConstantValue.Value is not string s) continue;
            if (!IsLocalizableText(s)) continue;                                    // single glyphs / format tokens allowed
            ctx.ReportDiagnostic(Diagnostic.Create(
                LocalizationDiagnostics.RawDrawString, part.Syntax.GetLocation()));
        }
    }

    // Shared length/letter gate: only text of length > 1 that contains a letter is player-facing copy. Single
    // glyphs (a close 'X') and letter-free tokens (numbers, "{0}", " - ") are not flagged.
    private static bool IsLocalizableText(string s) => s.Length > 1 && s.Any(char.IsLetter);

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

    // True when the node sits lexically inside a `#if` branch that is BOTH taken AND live because DEBUG is
    // defined (`#if DEBUG`, `#if DEBUG || TRACE`, ...). A raw `Condition.ToString().Contains("DEBUG")` test also
    // matched `#if !DEBUG` - the inverse, which is the branch that goes live in a Release build - and so silently
    // exempted release-only code from KELOC002/KELOC003 (issue #165). The condition is parsed instead, and only a
    // non-negated DEBUG identifier is treated as a debug carve-out.
    private static bool IsInsideActiveDebugRegion(SyntaxNode node)
    {
        SyntaxNode root = node.SyntaxTree.GetRoot();
        foreach (var ifDir in root.DescendantNodes(descendIntoTrivia: true).OfType<IfDirectiveTriviaSyntax>())
        {
            if (!ifDir.BranchTaken) continue;
            if (!ConditionEnablesDebug(ifDir.Condition)) continue;

            var related = ifDir.GetRelatedDirectives();
            int idx = related.IndexOf(ifDir);
            if (idx < 0 || idx + 1 >= related.Count) continue;

            // The taken branch runs from just after the #if to the next related directive (#elif/#else/#endif).
            DirectiveTriviaSyntax next = related[idx + 1];
            if (node.SpanStart >= ifDir.Span.End && node.Span.End <= next.SpanStart) return true;
        }
        return false;
    }

    // Whether an `#if` condition goes live because DEBUG is defined: DEBUG appears as a whole identifier in a
    // non-negated position. `#if DEBUG` and `#if DEBUG || TRACE` qualify. `#if !DEBUG` does not - the DEBUG token
    // sits under one logical-not, so it is the Release-live inverse, not a debug carve-out. A substring test
    // cannot tell these apart because "!DEBUG".Contains("DEBUG") is true (issue #165).
    private static bool ConditionEnablesDebug(ExpressionSyntax condition)
    {
        foreach (IdentifierNameSyntax id in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.ValueText != "DEBUG") continue;

            // Count the enclosing `!` operators up to the condition root; an even count is a positive position.
            int negations = 0;
            for (SyntaxNode? p = id; p is not null; p = p.Parent)
            {
                if (p.IsKind(SyntaxKind.LogicalNotExpression)) negations++;
                if (p == condition) break;
            }
            if (negations % 2 == 0) return true;
        }
        return false;
    }
}
