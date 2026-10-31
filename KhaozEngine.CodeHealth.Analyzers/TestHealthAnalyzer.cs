using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Checks per-file test-health debt against .testhealth-baseline. Without a baseline or xunit,
    /// the compilation has not adopted the ratchet. Report mode inventories non-exempt debt.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class TestHealthAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            TestHealthDiagnostics.ExceptionMessageAssertion,
            TestHealthDiagnostics.NonPublicReflection,
            TestHealthDiagnostics.LoopOnlyAssertion);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var baselineFile = context.Options.AdditionalFiles.FirstOrDefault(
                file => Path.GetFileName(file.Path) == ".testhealth-baseline");
            if (baselineFile is null) return;

            var symbols = TestHealthSymbols.Resolve(context.Compilation);
            if (symbols is null) return;

            var text = baselineFile.GetText(context.CancellationToken);
            if (text is null) return;

            var baseline = TestHealthBaseline.Parse(text.ToString());
            var root = NormalizeSeparators(Path.GetDirectoryName(baselineFile.Path) ?? string.Empty);
            var reportMode = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.KhaozTestHealthMode", out var mode) && mode == "report";
            context.RegisterSemanticModelAction(modelContext => CheckTree(modelContext, symbols, baseline, root, reportMode));
        }

        private static void CheckTree(SemanticModelAnalysisContext context, TestHealthSymbols symbols,
            TestHealthBaseline baseline, string root, bool reportMode)
        {
            if (context.IsGeneratedCode) return;

            var path = context.SemanticModel.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(path)) return;

            var full = NormalizeSeparators(path);
            if (!full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return;
            var relative = full.Substring(root.Length + 1);
            if (IsExcluded(relative)) return;

            ReportFindings(context, baseline, relative, reportMode, TestHealthDiagnostics.NonPublicReflection,
                PrivateReflectionDetector.Find(context.SemanticModel, symbols, context.CancellationToken));
        }

        private static void ReportFindings(SemanticModelAnalysisContext context, TestHealthBaseline baseline,
            string relative, bool reportMode, DiagnosticDescriptor descriptor, IEnumerable<Location> findings)
        {
            if (baseline.IsExempt(descriptor.Id, relative)) return;

            var locations = findings.ToImmutableArray();
            var allowance = reportMode ? 0 : baseline.AllowanceFor(descriptor.Id, relative);
            if (locations.Length <= allowance) return;

            foreach (var location in locations)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location,
                    effectiveSeverity: reportMode ? DiagnosticSeverity.Warning : descriptor.DefaultSeverity,
                    additionalLocations: null, properties: null,
                    messageArgs: new object[] { relative, locations.Length, allowance }));
            }
        }

        // Keep the path exclusions in step with FileSizeAnalyzer.
        private static bool IsExcluded(string relative)
        {
            if (relative.StartsWith("obj/", StringComparison.Ordinal) ||
                relative.StartsWith("bin/", StringComparison.Ordinal) ||
                relative.StartsWith("vendor/", StringComparison.Ordinal) ||
                relative.Contains("/obj/") ||
                relative.Contains("/bin/") ||
                relative.Contains("/vendor/"))
            {
                return true;
            }
            return relative.EndsWith(".Designer.cs", StringComparison.Ordinal) ||
                   relative.EndsWith(".g.cs", StringComparison.Ordinal) ||
                   relative.EndsWith(".generated.cs", StringComparison.Ordinal) ||
                   relative.EndsWith(".AssemblyInfo.cs", StringComparison.Ordinal);
        }

        private static string NormalizeSeparators(string path) => path.Replace('\\', '/');
    }
}
