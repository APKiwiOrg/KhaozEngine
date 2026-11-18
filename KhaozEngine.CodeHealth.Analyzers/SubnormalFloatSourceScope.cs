using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Applies Roslyn's source exclusions to the deferred generic walk.</summary>
    internal sealed class SubnormalFloatSourceScope
    {
        private readonly Compilation _compilation;
        private readonly AnalyzerConfigOptionsProvider _options;
        private readonly CancellationToken _cancellationToken;
        private readonly INamedTypeSymbol? _generatedAttribute;
        private readonly Dictionary<SyntaxTree, bool> _generatedTrees = new Dictionary<SyntaxTree, bool>();
        private readonly Dictionary<SyntaxNode, ISymbol?> _declarations = new Dictionary<SyntaxNode, ISymbol?>();
        private readonly Dictionary<ISymbol, bool> _generatedSymbols = new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);

        public SubnormalFloatSourceScope(CompilationAnalysisContext context)
        {
            _compilation = context.Compilation;
            _options = context.Options.AnalyzerConfigOptionsProvider;
            _cancellationToken = context.CancellationToken;
            _generatedAttribute = _compilation.GetTypeByMetadataName("System.CodeDom.Compiler.GeneratedCodeAttribute");
        }

        public bool ShouldAnalyzeTree(SyntaxTree tree)
        {
            if (IsGeneratedTree(tree)) return false;
            var options = _compilation.Options;
            if (options.SyntaxTreeOptionsProvider is not { } provider) return true;

            var rule = SubnormalFloatDiagnostics.SubnormalFloatConstant;
            var severity = rule.GetEffectiveSeverity(options);
            if (provider.TryGetDiagnosticValue(tree, rule.Id, _cancellationToken, out var configured) ||
                provider.TryGetGlobalDiagnosticValue(rule.Id, _cancellationToken, out configured))
                severity = configured;
            return severity != ReportDiagnostic.Suppress;
        }

        private bool IsGeneratedTree(SyntaxTree tree)
        {
            if (!_generatedTrees.TryGetValue(tree, out var generated))
            {
                generated = ClassifyTree(tree);
                _generatedTrees.Add(tree, generated);
            }
            return generated;
        }

        public bool IsGenerated(SyntaxNode node, SemanticModel model)
        {
            if (IsHidden(node.SyntaxTree, node.SpanStart)) return true;
            foreach (var ancestor in node.AncestorsAndSelf())
            {
                // Roslyn analyzes local functions and lambdas as part of their containing member.
                // Use that declaration for signatures and initializers as well as bodies.
                var declaration = ancestor switch
                {
                    BaseFieldDeclarationSyntax field when field.Declaration.Variables.Count != 0 =>
                        field.Declaration.Variables[0],
                    MemberDeclarationSyntax or AccessorDeclarationSyntax or
                        VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } => ancestor,
                    _ => null,
                };
                if (declaration is null) continue;
                if (!_declarations.TryGetValue(declaration, out var symbol))
                {
                    symbol = model.GetDeclaredSymbol(declaration, _cancellationToken);
                    _declarations.Add(declaration, symbol);
                }
                if (symbol != null) return IsGeneratedSymbol(symbol);
            }
            return false;
        }

        private bool ClassifyTree(SyntaxTree tree)
        {
            if (_options.GetOptions(tree).TryGetValue("generated_code", out var value) &&
                bool.TryParse(value, out var generated))
                return generated;

            var configured = _compilation.Options.SyntaxTreeOptionsProvider?.IsGenerated(tree, _cancellationToken);
            if (configured is GeneratedKind.MarkedGenerated or GeneratedKind.NotGenerated)
                return configured == GeneratedKind.MarkedGenerated;

            // Match the compiler's filename and leading-comment fallback when no override is set.
            string path = tree.FilePath ?? "";
            string file = path.Substring(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1);
            if (file.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase)) return true;
            int extension = file.LastIndexOf('.');
            if (extension > 0)
            {
                string name = file.Substring(0, extension);
                if (name.EndsWith(".designer", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".generated", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".g", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".g.i", StringComparison.OrdinalIgnoreCase)) return true;
            }

            foreach (var trivia in tree.GetRoot(_cancellationToken).GetLeadingTrivia())
            {
                if (trivia.Kind() is not (SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia))
                    continue;
                string comment = trivia.ToString();
                if (comment.Contains("<autogenerated") || comment.Contains("<auto-generated")) return true;
            }
            return false;
        }

        private bool IsGeneratedSymbol(ISymbol symbol)
        {
            if (_generatedSymbols.TryGetValue(symbol, out var generated)) return generated;
            generated = HasGeneratedAttribute(symbol) || HasOnlyGeneratedDeclarations(symbol);
            _generatedSymbols.Add(symbol, generated);
            return generated;
        }

        private bool HasGeneratedAttribute(ISymbol symbol)
        {
            if (_generatedAttribute is null) return false;
            for (ISymbol? current = symbol; current != null; current = current.ContainingSymbol)
            {
                if (current.Kind == SymbolKind.Namespace) continue;
                if (current.DeclaringSyntaxReferences.Length > 1) return false;
                foreach (var attribute in current.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _generatedAttribute))
                        return true;
                }
            }
            return false;
        }

        private bool HasOnlyGeneratedDeclarations(ISymbol symbol)
        {
            var references = symbol.DeclaringSyntaxReferences;
            foreach (var reference in references)
            {
                if (!IsGeneratedTree(reference.SyntaxTree) && !IsHidden(reference.SyntaxTree, reference.Span.Start))
                    return false;
            }
            return !references.IsEmpty;
        }

        private bool IsHidden(SyntaxTree tree, int position) =>
            tree.GetLineVisibility(position, _cancellationToken) == LineVisibility.Hidden;
    }
}
