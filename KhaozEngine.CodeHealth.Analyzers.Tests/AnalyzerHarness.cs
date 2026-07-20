using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using KhaozEngine.CodeHealth.Analyzers;

namespace KhaozEngine.CodeHealth.Analyzers.Tests;

internal static class AnalyzerHarness
{
    /// <summary>Fake repo root. Purely a string prefix: the analyzer never touches the filesystem.</summary>
    public const string Root = "/repo";

    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => p.Length > 0)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    /// <summary>
    /// Compiles the given (path, content) sources and runs FileSizeAnalyzer with an in-memory
    /// .filesize-baseline at Root (null baseline = repo has not adopted). Returns KESIZE diagnostics.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> Run(
        (string Path, string Content)[] sources, string? baseline, string? capOverride = null)
    {
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s.Content, path: s.Path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "FileSizeHarness", trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additional = baseline is null
            ? ImmutableArray<AdditionalText>.Empty
            : ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(Root + "/.filesize-baseline", baseline));

        var options = new AnalyzerOptions(additional, new TestConfigOptionsProvider(capOverride));
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new FileSizeAnalyzer()), options);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(d => d.Id.StartsWith("KESIZE", StringComparison.Ordinal))
            .ToImmutableArray();
    }

    /// <summary>A compilable source with exactly <paramref name="newlines"/> newline characters
    /// (wc -l parity: the final "class C {}" line has no trailing newline and does not count).</summary>
    public static string SourceOfLines(int newlines) =>
        string.Concat(Enumerable.Repeat("// filler\n", newlines)) + "class C {}";

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;
        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class TestConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private sealed class Options : AnalyzerConfigOptions
        {
            private readonly string? _cap;
            public Options(string? cap) => _cap = cap;
            public override bool TryGetValue(string key, out string value)
            {
                if (_cap is not null && key == "build_property.KhaozFileSizeCap")
                {
                    value = _cap;
                    return true;
                }
                value = null!;
                return false;
            }
        }
        private readonly Options _options;
        public TestConfigOptionsProvider(string? cap) => _options = new Options(cap);
        public override AnalyzerConfigOptions GlobalOptions => _options;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }
}
