using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// Compile-time twin of the fleet's scripts/check-file-size.sh ratchet. Reads .filesize-baseline
    /// from AdditionalFiles (no baseline means the repo has not adopted the ratchet, so the analyzer
    /// stays silent), then checks every syntax tree: a baselined file must not exceed its recorded
    /// line count (KESIZE001), an unlisted file must stay under the cap (KESIZE002, default 800,
    /// overridable via the KhaozFileSizeCap compiler-visible property). A path on an "exempt" line in
    /// the baseline is not checked at all: no baseline, no cap, no diagnostic, for files whose size is
    /// content rather than structure. Line count is wc -l parity: the number of newline characters in
    /// the file.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class FileSizeAnalyzer : DiagnosticAnalyzer
    {
        public const int DefaultCap = 800;

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(FileSizeDiagnostics.FileGrewPastBaseline, FileSizeDiagnostics.FileOverCap);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var baseline = context.Options.AdditionalFiles.FirstOrDefault(
                f => Path.GetFileName(f.Path) == ".filesize-baseline");
            if (baseline is null) return;

            var baselineText = baseline.GetText(context.CancellationToken);
            if (baselineText is null) return;

            var parsed = BaselineFile.Parse(baselineText.ToString());
            var root = AnalyzerPathPolicy.NormalizeSeparators(Path.GetDirectoryName(baseline.Path) ?? string.Empty);

            var cap = DefaultCap;
            if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                    "build_property.KhaozFileSizeCap", out var capText)
                && int.TryParse(capText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCap))
            {
                cap = parsedCap;
            }

            context.RegisterSyntaxTreeAction(treeContext => CheckTree(treeContext, parsed, root, cap));
        }

        private static void CheckTree(
            SyntaxTreeAnalysisContext context, BaselineFile.Baseline baseline, string root, int cap)
        {
            if (context.IsGeneratedCode) return;
            if (!AnalyzerPathPolicy.TryGetCheckedRelativePath(context.Tree.FilePath, root, out var relative)) return;
            if (baseline.IsExempt(relative)) return;

            var text = context.Tree.GetText(context.CancellationToken);
            var lineCount = CountNewlines(text);

            if (baseline.Entries.TryGetValue(relative, out var limit))
            {
                if (lineCount > limit)
                    Report(context, FileSizeDiagnostics.FileGrewPastBaseline, text, relative, lineCount, limit);
            }
            else if (lineCount > cap)
            {
                Report(context, FileSizeDiagnostics.FileOverCap, text, relative, lineCount, cap);
            }
        }

        // Anchors the diagnostic on the first line past the limit, so the IDE jumps to where the
        // overflow starts rather than to line 1 of a multi-thousand-line file.
        private static void Report(
            SyntaxTreeAnalysisContext context, DiagnosticDescriptor descriptor, SourceText text,
            string relative, int lineCount, int limit)
        {
            var line = text.Lines[Math.Min(limit, text.Lines.Count - 1)];
            var location = Location.Create(context.Tree, line.Span);
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, relative, lineCount, limit));
        }

        // wc -l parity: a line is a newline character, so a final line without a trailing newline
        // does not count. SourceText.Lines.Count would be off by one there.
        private static int CountNewlines(SourceText text)
        {
            var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }
    }
}
