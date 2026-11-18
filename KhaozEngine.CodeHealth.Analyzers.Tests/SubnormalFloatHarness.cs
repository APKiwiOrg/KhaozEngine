using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.CodeHealth.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.Tests.CodeHealth;

internal static class SubnormalFloatHarness
{
    private static readonly string[] PlatformReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.Length > 0 &&
            !Path.GetFileNameWithoutExtension(path).StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    /// <summary>The tracked FloatUlp template, embedded from build/FloatUlp.cs.</summary>
    public static string HelperTemplate()
    {
        using var stream = typeof(SubnormalFloatHarness).Assembly.GetManifestResourceStream("FloatUlp.template.cs")
            ?? throw new InvalidOperationException("The FloatUlp template is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Wraps members in a class with the usings every sample needs.</summary>
    public static string Members(string members) =>
        "using System;\nusing System.Numerics;\nusing System.Runtime.CompilerServices;\n" +
        "public sealed class Sample\n{\n" + members + "\n}\n";

    /// <summary>
    /// Compiles the sources and runs KEFP001. <paramref name="scope"/> is the KhaozSubnormalFloatRule value, null for
    /// a compilation that does not surface the property at all. Refuses a source that does not compile, so no case
    /// passes because its sample failed to bind.
    /// </summary>
    public static Task<ImmutableArray<Diagnostic>> Run(string? scope, bool withXunit, bool warningsAsErrors,
        params string[] sources) =>
        Run(scope, withXunit, warningsAsErrors, checkOverflow: false, sources);

    public static async Task<ImmutableArray<Diagnostic>> Run(string? scope, bool withXunit, bool warningsAsErrors,
        bool checkOverflow, params string[] sources)
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
        var trees = sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, path: $"/repo/Sample{index}.cs"));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            checkOverflow: checkOverflow, generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default);
        var compilation = CSharpCompilation.Create("SubnormalFloatHarness", trees, references, options);
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException("Sample does not compile: " + string.Join("; ", errors.Select(e => e.ToString())));

        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new ScopeOptionsProvider(scope));
        return await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new SubnormalFloatAnalyzer()), analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>The source text each diagnostic points at, in source order.</summary>
    public static string[] Flagged(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Where(diagnostic => diagnostic.Id == "KEFP001")
            .OrderBy(diagnostic => diagnostic.Location.SourceTree!.FilePath, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .Select(diagnostic => diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan))
            .ToArray();

    private sealed class ScopeOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private sealed class Options : AnalyzerConfigOptions
        {
            private readonly string? _scope;
            public Options(string? scope) => _scope = scope;

            public override bool TryGetValue(string key, out string value)
            {
                if (_scope is not null && key == "build_property." + SubnormalFloatDiagnostics.ScopeProperty)
                {
                    value = _scope;
                    return true;
                }
                value = null!;
                return false;
            }
        }

        private readonly Options _options;
        public ScopeOptionsProvider(string? scope) => _options = new Options(scope);
        public override AnalyzerConfigOptions GlobalOptions => _options;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }
}
