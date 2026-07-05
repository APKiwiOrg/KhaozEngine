using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit.Sdk;

namespace KhaozEngine.Localization.Analyzers.Tests
{
    /// <summary>
    /// Compiles a C# snippet (plus minimal KhaozEngine.App stubs) and runs <see cref="LocalizationAnalyzer"/>
    /// against it, returning only the KELOC diagnostics. Hand-rolled so the test project needs no external
    /// analyzer-testing package (which churns versions independently of Roslyn).
    /// </summary>
    internal static class AnalyzerHarness
    {
        // Stand-ins for the App types so snippets compile without referencing KhaozEngine.App. The analyzer keys
        // off the fully-qualified names, which match these.
        private const string Stubs = @"
namespace KhaozEngine.App {
    using System;
    [AttributeUsage(AttributeTargets.All)] public sealed class LocalizationExemptAttribute : Attribute {}
    [AttributeUsage(AttributeTargets.Method|AttributeTargets.Constructor)] public sealed class LocalizationStringSinkAttribute : Attribute {}
    public readonly struct StringId { public StringId(string k){} }
    public readonly struct LocalizedText {
        public static implicit operator LocalizedText(StringId id) => default;
        public static LocalizedText Raw(string s) => default;
    }
}
";

        private static readonly MetadataReference[] References =
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        public static Task<ImmutableArray<Diagnostic>> Run(string source) => Run(source, null);

        public static Task<ImmutableArray<Diagnostic>> RunWithDebug(string source) =>
            Run(source, new CSharpParseOptions().WithPreprocessorSymbols("DEBUG"));

        public static async Task<ImmutableArray<Diagnostic>> Run(string source, CSharpParseOptions? parseOptions)
        {
            var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
            var stubTree = CSharpSyntaxTree.ParseText(Stubs, parseOptions);
            var compilation = CSharpCompilation.Create(
                "AnalyzerTestAsm",
                new[] { tree, stubTree },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Fail loudly if the snippet itself does not compile, so a broken test snippet is not read as "no diagnostics".
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
                throw new XunitException("snippet failed to compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));

            var withAnalyzers = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new LocalizationAnalyzer()));
            var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
            return diags.Where(d => d.Id.StartsWith("KELOC", StringComparison.Ordinal)).ToImmutableArray();
        }
    }
}
