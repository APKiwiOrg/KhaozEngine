using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.CodeHealth.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace KhaozEngine.Tests.CodeHealth;

internal static class TestHealthHarness
{
    private static readonly string[] PlatformReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.Length > 0 &&
            !Path.GetFileNameWithoutExtension(path).StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    public static async Task<ImmutableArray<Diagnostic>> Run(
        (string Path, string Content)[] sources, string? baseline, string? mode = null, bool withXunit = true)
    {
        var referencePaths = withXunit
            ? PlatformReferences.Concat(new[]
            {
                typeof(Xunit.Assert).Assembly.Location,
                typeof(Xunit.FactAttribute).Assembly.Location,
            })
            : PlatformReferences;
        var references = referencePaths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.Content, path: source.Path));
        var compilation = CSharpCompilation.Create(
            "TestHealthHarness", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additional = baseline is null
            ? ImmutableArray<AdditionalText>.Empty
            : ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText("/repo/.testhealth-baseline", baseline));
        var options = new AnalyzerOptions(additional, new TestConfigOptionsProvider(mode));
        var diagnostics = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new TestHealthAnalyzer()), options)
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(diagnostic => diagnostic.Id.StartsWith("KETEST", StringComparison.Ordinal))
            .ToImmutableArray();
    }

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
            private readonly string? _mode;
            public Options(string? mode) => _mode = mode;

            public override bool TryGetValue(string key, out string value)
            {
                if (_mode is not null && key == "build_property.KhaozTestHealthMode")
                {
                    value = _mode;
                    return true;
                }
                value = null!;
                return false;
            }
        }

        private readonly Options _options;
        public TestConfigOptionsProvider(string? mode) => _options = new Options(mode);
        public override AnalyzerConfigOptions GlobalOptions => _options;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }
}
