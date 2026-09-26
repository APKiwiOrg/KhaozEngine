using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace KhaozEngine.Localization.Analyzers.Tests
{
    internal static class GeneratorHarness
    {
        private const string Stubs = @"
namespace KhaozEngine.App
{
    public readonly struct StringId
    {
        public StringId(string key) { }
    }
}
";

        private static readonly MetadataReference[] References =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        public static GeneratorHarnessResult Run(
            string path,
            string content,
            IReadOnlyDictionary<string, string>? metadata = null,
            string additionalSource = "") =>
            Run(new[] { new GeneratorAdditionalFile(path, content, metadata) }, additionalSource);

        public static GeneratorHarnessResult Run(
            IReadOnlyList<GeneratorAdditionalFile> files,
            string additionalSource = "")
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            ImmutableArray<AdditionalText> additionalTexts = files
                .Select(file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Content))
                .ToImmutableArray();
            var compilation = CSharpCompilation.Create(
                "GeneratorTestAsm",
                new[]
                {
                    CSharpSyntaxTree.ParseText(Stubs, parseOptions),
                    CSharpSyntaxTree.ParseText(additionalSource, parseOptions),
                },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                ImmutableArray.Create(new StringIdGenerator().AsSourceGenerator()),
                additionalTexts,
                parseOptions,
                new TestAnalyzerConfigOptionsProvider(files));

            driver = driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out Compilation updatedCompilation,
                out ImmutableArray<Diagnostic> driverDiagnostics);

            GeneratorDriverRunResult run = driver.GetRunResult();
            ImmutableArray<string> sources = run.Results
                .SelectMany(result => result.GeneratedSources)
                .Select(source => source.SourceText.ToString())
                .ToImmutableArray();
            ImmutableArray<Diagnostic> diagnostics = driverDiagnostics
                .AddRange(run.Diagnostics)
                .Distinct(DiagnosticComparer.Instance)
                .ToImmutableArray();
            ImmutableArray<Diagnostic> compilationErrors = updatedCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();

            return new GeneratorHarnessResult(sources, diagnostics, compilationErrors);
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

            public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default) => _text;
        }

        private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
        {
            private readonly IReadOnlyDictionary<string, AnalyzerConfigOptions> _fileOptions;

            public TestAnalyzerConfigOptionsProvider(IReadOnlyList<GeneratorAdditionalFile> files)
            {
                _fileOptions = files.ToDictionary(
                    file => file.Path,
                    file => (AnalyzerConfigOptions)new TestAnalyzerConfigOptions(file.Metadata),
                    StringComparer.Ordinal);
            }

            public override AnalyzerConfigOptions GlobalOptions { get; } = new TestAnalyzerConfigOptions(null);

            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestAnalyzerConfigOptions(null);

            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
                _fileOptions.TryGetValue(textFile.Path, out AnalyzerConfigOptions? options)
                    ? options
                    : new TestAnalyzerConfigOptions(null);
        }

        private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
        {
            private readonly IReadOnlyDictionary<string, string> _options;

            public TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string>? options)
            {
                _options = options ?? ImmutableDictionary<string, string>.Empty;
            }

            public override bool TryGetValue(string key, out string value) => _options.TryGetValue(key, out value!);
        }

        private sealed class DiagnosticComparer : IEqualityComparer<Diagnostic>
        {
            public static DiagnosticComparer Instance { get; } = new();

            public bool Equals(Diagnostic? x, Diagnostic? y) =>
                x?.Id == y?.Id &&
                x?.Location.SourceSpan == y?.Location.SourceSpan &&
                x?.GetMessage() == y?.GetMessage();

            public int GetHashCode(Diagnostic obj) => HashCode.Combine(obj.Id, obj.Location.SourceSpan, obj.GetMessage());
        }
    }

    internal sealed record GeneratorHarnessResult(
        ImmutableArray<string> Sources,
        ImmutableArray<Diagnostic> Diagnostics,
        ImmutableArray<Diagnostic> CompilationErrors);

    internal sealed record GeneratorAdditionalFile(
        string Path,
        string Content,
        IReadOnlyDictionary<string, string>? Metadata);
}
