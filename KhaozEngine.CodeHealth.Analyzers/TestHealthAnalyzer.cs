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
            var root = AnalyzerPathPolicy.NormalizeSeparators(Path.GetDirectoryName(baselineFile.Path) ?? string.Empty);
            var reportMode = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.KhaozTestHealthMode", out var mode) && mode == "report";
            context.RegisterSemanticModelAction(modelContext => CheckTree(modelContext, symbols, baseline, root, reportMode));
        }

        private static void CheckTree(SemanticModelAnalysisContext context, TestHealthSymbols symbols,
            TestHealthBaseline baseline, string root, bool reportMode)
        {
            if (context.IsGeneratedCode) return;
            if (!AnalyzerPathPolicy.TryGetCheckedRelativePath(
                    context.SemanticModel.SyntaxTree.FilePath, root, out var relative)) return;

            ReportFindings(context, baseline, relative, reportMode, TestHealthDiagnostics.ExceptionMessageAssertion,
                ExceptionMessageAssertionDetector.Find(context.SemanticModel, symbols, context.CancellationToken));
            ReportFindings(context, baseline, relative, reportMode, TestHealthDiagnostics.NonPublicReflection,
                PrivateReflectionDetector.Find(context.SemanticModel, symbols, context.CancellationToken));
            // KETEST003 is a review aid, never a ratchet. On this repository only 6 of 20 sampled findings
            // were real: loops over local tables and test-owned case helpers look possibly empty to a
            // syntactic check. Report mode lists its candidates for review, normal builds never see it.
            if (reportMode)
                ReportFindings(context, baseline, relative, reportMode, TestHealthDiagnostics.LoopOnlyAssertion,
                    LoopOnlyAssertionDetector.Find(context.SemanticModel, symbols, context.CancellationToken));
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
    }
}
