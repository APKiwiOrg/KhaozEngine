using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace KhaozEngine.Localization.Analyzers;

/// <summary>Generates typed localization keys from explicitly opted neutral resx files.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class StringIdGenerator : IIncrementalGenerator
{
    private const string TargetTypeKey = "build_metadata.AdditionalFiles.KhaozStringIdType";
    private const string AccessibilityKey = "build_metadata.AdditionalFiles.KhaozStringIdAccessibility";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GenerationRequest?> inputs = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ReadInput(pair.Left, pair.Right, cancellationToken));

        context.RegisterSourceOutput(
            inputs.Where(static input => input is not null).Collect().Combine(context.CompilationProvider),
            static (productionContext, pair) => Execute(productionContext, pair.Left, pair.Right));
    }

    private static GenerationRequest? ReadInput(
        AdditionalText file,
        AnalyzerConfigOptionsProvider optionsProvider,
        System.Threading.CancellationToken cancellationToken)
    {
        if (!optionsProvider.GetOptions(file).TryGetValue(TargetTypeKey, out string? targetType) ||
            string.IsNullOrWhiteSpace(targetType))
        {
            return null;
        }

        if (!file.Path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            return InvalidConfiguration(file, "the opted AdditionalFile must use the .resx extension");

        string[] targetParts = targetType.Split('.');
        if (targetParts.Any(static part => !SyntaxFacts.IsValidIdentifier(part) ||
            SyntaxFacts.GetKeywordKind(part) != SyntaxKind.None))
            return InvalidConfiguration(file, $"'{targetType}' is not a valid fully qualified C# type name");

        optionsProvider.GetOptions(file).TryGetValue(AccessibilityKey, out string? configuredAccessibility);
        string accessibility = string.IsNullOrWhiteSpace(configuredAccessibility)
            ? "internal"
            : configuredAccessibility!.ToLowerInvariant();
        if (accessibility != "internal" && accessibility != "public")
            return InvalidConfiguration(file, "KhaozStringIdAccessibility must be 'internal' or 'public'");

        SourceText? text = file.GetText(cancellationToken);
        if (text is null) return null;

        XDocument document;
        try
        {
            document = XDocument.Parse(text.ToString());
        }
        catch (XmlException exception)
        {
            return GenerationRequest.FromDiagnostic(Diagnostic.Create(
                StringIdGeneratorDiagnostics.InvalidResx,
                Location.Create(file.Path, default, default),
                Path.GetFileName(file.Path),
                exception.Message));
        }

        int separator = targetType.LastIndexOf('.');
        string targetNamespace = separator < 0 ? "" : targetType.Substring(0, separator);
        string targetName = separator < 0 ? targetType : targetType.Substring(separator + 1);
        if (document.Root is not { } root || root.Name.LocalName != "root")
            return InvalidResx(file, "the document must have a root element named 'root'");

        var keysBuilder = ImmutableArray.CreateBuilder<ResourceKey>();
        foreach (XElement element in root.Elements().Where(static element => element.Name.LocalName == "data"))
        {
            if (!IsStringData(element)) continue;

            string? key = (string?)element.Attribute("name");
            if (key is null || key.Length == 0)
                return InvalidResx(file, "a string data element has an empty resource key");

            string memberName = ToMemberName(key);
            if (memberName.Length == 0)
                return InvalidResx(file, $"resource key '{key}' cannot produce a C# member name");
            if (string.Equals(memberName, targetName, StringComparison.Ordinal))
                return InvalidResx(file, $"resource key '{key}' produces member '{memberName}', which matches the target type name");

            keysBuilder.Add(new ResourceKey(key, memberName));
        }

        ImmutableArray<ResourceKey> keys = keysBuilder
            .OrderBy(static key => key.Key, StringComparer.Ordinal)
            .ToImmutableArray();

        IGrouping<string, ResourceKey>? collision = keys
            .GroupBy(static key => key.MemberName, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (collision is not null)
        {
            ResourceKey[] collidingKeys = collision.Take(2).ToArray();
            return GenerationRequest.FromDiagnostic(Diagnostic.Create(
                StringIdGeneratorDiagnostics.MemberCollision,
                Location.Create(file.Path, default, default),
                collidingKeys[0].Key,
                collidingKeys[1].Key,
                collision.Key,
                Path.GetFileName(file.Path)));
        }

        return GenerationRequest.FromInput(new ResourceInput(
                file.Path,
                targetNamespace,
                targetName,
                accessibility,
                keys));
    }

    private static GenerationRequest InvalidConfiguration(AdditionalText file, string reason) =>
        GenerationRequest.FromDiagnostic(Diagnostic.Create(
            StringIdGeneratorDiagnostics.InvalidConfiguration,
            Location.Create(file.Path, default, default),
            Path.GetFileName(file.Path),
            reason));

    private static GenerationRequest InvalidResx(AdditionalText file, string reason) =>
        GenerationRequest.FromDiagnostic(Diagnostic.Create(
            StringIdGeneratorDiagnostics.InvalidResx,
            Location.Create(file.Path, default, default),
            Path.GetFileName(file.Path),
            reason));

    private static bool IsStringData(XElement element)
    {
        if (element.Attribute("mimetype") is not null) return false;
        string? type = (string?)element.Attribute("type");
        return type is null ||
            type == "System.String" ||
            type.StartsWith("System.String,", StringComparison.Ordinal);
    }

    private static string ToMemberName(string key)
    {
        var builder = new StringBuilder(key.Length);
        bool capitalize = true;
        foreach (char character in key)
        {
            if (!char.IsLetterOrDigit(character))
            {
                capitalize = true;
                continue;
            }

            builder.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }

        if (builder.Length > 0 && char.IsDigit(builder[0])) builder.Insert(0, '_');
        return builder.ToString();
    }

    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<GenerationRequest?> requests,
        Compilation compilation)
    {
        var inputs = new List<ResourceInput>();
        foreach (GenerationRequest? request in requests)
        {
            if (request?.Diagnostic is { } diagnostic)
            {
                context.ReportDiagnostic(diagnostic);
                continue;
            }

            if (request?.Input is { } input) inputs.Add(input);
        }

        var collidingTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (IGrouping<string, ResourceInput> group in inputs.GroupBy(
                     static input => input.MetadataName,
                     StringComparer.Ordinal))
        {
            ResourceInput[] targets = group.OrderBy(static input => input.Path, StringComparer.Ordinal).Take(2).ToArray();
            if (targets.Length < 2) continue;

            collidingTargets.Add(group.Key);
            context.ReportDiagnostic(Diagnostic.Create(
                StringIdGeneratorDiagnostics.TargetCollision,
                Location.Create(targets[0].Path, default, default),
                group.Key,
                Path.GetFileName(targets[0].Path),
                $"it is also targeted by '{Path.GetFileName(targets[1].Path)}'"));
        }

        foreach (ResourceInput input in inputs.OrderBy(static input => input.Path, StringComparer.Ordinal))
        {
            if (collidingTargets.Contains(input.MetadataName)) continue;

            if (compilation.GetTypeByMetadataName(input.MetadataName) is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    StringIdGeneratorDiagnostics.TargetCollision,
                    Location.Create(input.Path, default, default),
                    input.MetadataName,
                    Path.GetFileName(input.Path),
                    "that type already exists"));
                continue;
            }

            Emit(context, input);
        }
    }

    private static void Emit(SourceProductionContext context, ResourceInput input)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        if (input.Namespace.Length > 0)
        {
            source.Append("namespace ").Append(input.Namespace).AppendLine();
            source.AppendLine("{");
        }

        string indent = input.Namespace.Length > 0 ? "    " : "";
        source.Append(indent).Append(input.Accessibility).Append(" static class ").Append(input.TypeName).AppendLine();
        source.Append(indent).AppendLine("{");
        foreach (ResourceKey key in input.Keys)
        {
            source.Append(indent).Append("    public static readonly global::KhaozEngine.App.StringId ")
                .Append(key.MemberName)
                .Append(" = new(")
                .Append(SymbolDisplay.FormatLiteral(key.Key, quote: true))
                .AppendLine(");");
        }
        source.Append(indent).AppendLine("}");
        if (input.Namespace.Length > 0) source.AppendLine("}");

        context.AddSource(input.HintName, SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private sealed class ResourceInput
    {
        public ResourceInput(
            string path,
            string targetNamespace,
            string typeName,
            string accessibility,
            ImmutableArray<ResourceKey> keys)
        {
            Path = path;
            Namespace = targetNamespace;
            TypeName = typeName;
            Accessibility = accessibility;
            Keys = keys;
            HintName = (targetNamespace.Length == 0 ? typeName : targetNamespace + "." + typeName) +
                ".StringIds.g.cs";
        }

        public string Path { get; }
        public string Namespace { get; }
        public string TypeName { get; }
        public string Accessibility { get; }
        public ImmutableArray<ResourceKey> Keys { get; }
        public string HintName { get; }
        public string MetadataName => Namespace.Length == 0 ? TypeName : Namespace + "." + TypeName;
    }

    private sealed class GenerationRequest
    {
        private GenerationRequest(ResourceInput? input, Diagnostic? diagnostic)
        {
            Input = input;
            Diagnostic = diagnostic;
        }

        public ResourceInput? Input { get; }
        public Diagnostic? Diagnostic { get; }

        public static GenerationRequest FromInput(ResourceInput input) => new(input, null);
        public static GenerationRequest FromDiagnostic(Diagnostic diagnostic) => new(null, diagnostic);
    }

    private sealed class ResourceKey
    {
        public ResourceKey(string key, string memberName)
        {
            Key = key;
            MemberName = memberName;
        }

        public string Key { get; }
        public string MemberName { get; }
    }
}
