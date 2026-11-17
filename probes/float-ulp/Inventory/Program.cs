using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// #1832 inventory: every site where engine source puts a float the JIT can see as a subnormal constant.
// One merged compilation of every non-test project, so engine constants resolve across projects.
// Usage: Inventory <repo root> <output tsv> <unaccounted grep report>
static class Inventory
{
    const float SmallestNormal = 1.17549435E-38f;
    const double SmallestNormalDouble = 2.2250738585072014E-308;

    static readonly Regex TestProject = new(@"(Tests?$|\.Tests\.|TestSupport|TestKit|^test-shared)", RegexOptions.Compiled);
    static readonly Regex NonShipped = new(@"(^tools/|^MmoServerSample|^SnapshotTool|Benchmarks|AotProbe|\.Tool$|Showcase|^probes/)", RegexOptions.Compiled);
    static readonly Regex BoundName = new(@"(?i)(min|max|lo$|^lo|hi$|^hi|lower|upper|start|end|begin|bottom|top|floor|ceil|bound|extent|from|^to|near|far|first|last|left|right|enter|exit)", RegexOptions.Compiled);

    sealed record Site(string Project, string Kind, string File, int Line, string Form, string Argument, string JitKnownArgument,
        string Member, string Context, string AutoClass, string Snippet);

    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && !Rel(root, p).StartsWith("probes/"))
            .Select(p => (Dir: Path.GetDirectoryName(p)!, Name: Path.GetFileNameWithoutExtension(p)))
            .OrderByDescending(p => p.Dir.Length).ToList();

        var fileProject = new Dictionary<string, (string Name, string Kind)>();
        foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Rel(root, file);
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("probes/")) continue;
            var owner = projects.FirstOrDefault(p => file.StartsWith(p.Dir + "/"));
            string name = owner.Name ?? "(no project)";
            string projRel = owner.Dir is null ? rel : Rel(root, owner.Dir);
            string kind = TestProject.IsMatch(name) || rel.StartsWith("test-shared/") ? "test"
                : NonShipped.IsMatch(projRel) || NonShipped.IsMatch(name) ? "tool-or-sample"
                : owner.Name is null ? "no-project" : "engine";
            fileProject[file] = (name, kind);
        }

        var production = fileProject.Where(f => f.Value.Kind != "test").Select(f => f.Key).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var trees = production.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f),
            new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "NET", "NET10_0", "NET10_0_OR_GREATER", "RELEASE" }), path: f)).ToList();
        string refDir = Directory.GetDirectories(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "packs/Microsoft.NETCore.App.Ref"))
            .OrderBy(d => d, StringComparer.Ordinal).Last();
        var refs = Directory.GetFiles(Path.Combine(refDir, "ref/net10.0"), "*.dll").Select(d => MetadataReference.CreateFromFile(d));
        var compilation = CSharpCompilation.Create("merged", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        var sites = new List<Site>();
        var accounted = new HashSet<(string, int)>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var (project, kind) = fileProject[tree.FilePath];
            string[] lines = tree.GetText().Lines.Select(l => l.ToString()).ToArray();
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                string? form = null; string argument = ""; string jitKnown = "";
                if (node is InvocationExpressionSyntax invocation && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method)
                {
                    string type = method.ContainingType.ToDisplayString();
                    string name = method.Name;
                    bool step = (type == "System.MathF" || type == "System.Math" || type == "float" || type == "double")
                        && (name == "BitDecrement" || name == "BitIncrement");
                    bool reinterpret = (type == "System.BitConverter" && name.EndsWith("BitsToSingle", StringComparison.Ordinal))
                        || (type == "System.BitConverter" && name.EndsWith("BitsToDouble", StringComparison.Ordinal))
                        || (type == "System.Runtime.CompilerServices.Unsafe" && (name == "BitCast" || name == "As")
                            && method.TypeArguments.Any(t => t.SpecialType is SpecialType.System_Single or SpecialType.System_Double));
                    if (step || reinterpret)
                    {
                        var arg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                        var value = arg is null ? default : model.GetConstantValue(arg);
                        argument = arg?.ToString() ?? "";
                        jitKnown = value.HasValue ? $"constant {Describe(value.Value)}" : "runtime";
                        string precision = method.ReturnType.SpecialType == SpecialType.System_Double ? "double" : "float";
                        if (reinterpret && !value.HasValue) continue; // runtime bits are a bit move, never folded
                        form = step ? $"{type.Replace("System.", "")}.{name}({precision})" : $"{type.Replace("System.", "")}.{name}";
                        if (reinterpret) form += $" -> {ReinterpretResult(name, method, value.Value)}";
                    }
                }
                else
                {
                    var value = model.GetConstantValue(node);
                    if (!value.HasValue) continue;
                    bool subnormal = value.Value switch
                    {
                        float f => f != 0 && MathF.Abs(f) < SmallestNormal,
                        double d => d != 0 && Math.Abs(d) < SmallestNormalDouble,
                        _ => false,
                    };
                    if (!subnormal) continue;
                    // Report the outermost constant expression only.
                    if (node.Parent is ExpressionSyntax parent && model.GetConstantValue(parent).HasValue) continue;
                    if (node.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax inv }
                        && model.GetSymbolInfo(inv).Symbol is IMethodSymbol { Name: "BitDecrement" or "BitIncrement" }) continue;
                    form = value.Value is float ? "subnormal float constant" : "subnormal double constant";
                    argument = $"{node} = {Describe(value.Value)}";
                    jitKnown = "constant";
                }
                if (form is null) continue;
                var span = node.GetLocation().GetLineSpan();
                int line = span.StartLinePosition.Line + 1;
                string member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
                {
                    MethodDeclarationSyntax m => m.Identifier.Text,
                    ConstructorDeclarationSyntax c => ".ctor " + c.Identifier.Text,
                    PropertyDeclarationSyntax p => "prop " + p.Identifier.Text,
                    FieldDeclarationSyntax fd => "field " + string.Join(",", fd.Declaration.Variables.Select(v => v.Identifier.Text)),
                    OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
                    { } other => other.Kind().ToString(),
                    null => "",
                };
                var (context, auto) = Classify(node, model);
                accounted.Add((tree.FilePath, line));
                sites.Add(new Site(project, kind, Rel(root, tree.FilePath), line, form, argument, jitKnown, member, context, auto,
                    lines[line - 1].Trim()));
            }
        }

        using (var w = new StreamWriter(args[1]))
        {
            w.WriteLine("project\tkind\tfile\tline\tform\targument\tjit_known_argument\tmember\tcontext\tauto_class\tsnippet");
            foreach (var s in sites.OrderBy(s => s.Kind).ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line))
                w.WriteLine(string.Join('\t', s.Project, s.Kind, s.File, s.Line, s.Form, Clean(s.Argument), s.JitKnownArgument,
                    s.Member, Clean(s.Context), s.AutoClass, Clean(s.Snippet)));
        }

        // Cross-check: every grep-level hit in non-test source must be a recorded site, or is listed for review.
        var grep = new Regex(@"BitDecrement|BitIncrement|Epsilon|BitsToSingle|BitsToDouble|BitCast<|[0-9][eE]-(3[89]|4[0-9]|3[0-9][0-9])", RegexOptions.Compiled);
        using (var w = new StreamWriter(args[2]))
        {
            foreach (var tree in trees)
            {
                if (fileProject[tree.FilePath].Kind == "test") continue;
                var lines = tree.GetText().Lines;
                for (int i = 0; i < lines.Count; i++)
                {
                    string text = lines[i].ToString();
                    string trimmed = text.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;
                    if (grep.IsMatch(text) && !accounted.Contains((tree.FilePath, i + 1)))
                        w.WriteLine($"{Rel(root, tree.FilePath)}:{i + 1}\t{fileProject[tree.FilePath].Kind}\t{Clean(trimmed)}");
                }
            }
        }

        var errors = compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        Console.WriteLine($"files={production.Count} trees={trees.Count} compile-errors={errors} (expected: unresolved third-party types)");
        foreach (var g in sites.GroupBy(s => (s.Kind, s.Form)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Form))
            Console.WriteLine($"{g.Key.Kind}\t{g.Key.Form}\t{g.Count()} sites\t{g.Select(s => s.File).Distinct().Count()} files");
        return 0;
    }

    static (string Context, string Auto) Classify(ExpressionSyntax node, SemanticModel model)
    {
        SyntaxNode current = node;
        while (current.Parent is ParenthesizedExpressionSyntax or PrefixUnaryExpressionSyntax or CastExpressionSyntax
               or CheckedExpressionSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax or SwitchExpressionSyntax)
            current = current.Parent!;
        switch (current.Parent)
        {
            case BinaryExpressionSyntax b when b.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.EqualsExpression
                or SyntaxKind.NotEqualsExpression:
                return ($"compare {b.OperatorToken.Text}: {b}", "comparison bound");
            case BinaryExpressionSyntax b:
                return ($"arith {b.OperatorToken.Text}: {b}", "arithmetic operand");
            case ArgumentSyntax a when a.Parent?.Parent is InvocationExpressionSyntax inv:
            {
                var target = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                string name = target?.Name ?? (inv.Expression is MemberAccessExpressionSyntax ma ? ma.Name.Identifier.Text : inv.Expression.ToString());
                int index = ((ArgumentListSyntax)a.Parent).Arguments.IndexOf(a);
                string param = target is not null && index < target.Parameters.Length ? target.Parameters[index].Name : $"#{index}";
                if (name is "Min" or "Max" or "Clamp" or "MinMagnitude" or "MaxMagnitude" or "MinNumber" or "MaxNumber")
                    return ($"arg {name}.{param}: {inv}", "min or max operand");
                return ($"arg {name}.{param}: {inv}", BoundName.IsMatch(param) || BoundName.IsMatch(name) ? "interval end" : "value");
            }
            case ArgumentSyntax a when a.Parent?.Parent is BaseObjectCreationExpressionSyntax oc:
            {
                var target = model.GetSymbolInfo(oc).Symbol as IMethodSymbol;
                int index = ((ArgumentListSyntax)a.Parent).Arguments.IndexOf(a);
                string param = target is not null && index < target.Parameters.Length ? target.Parameters[index].Name : $"#{index}";
                return ($"new {target?.ContainingType.Name}.{param}: {oc}", BoundName.IsMatch(param) ? "interval end" : "value");
            }
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v } when v.Parent?.Parent is FieldDeclarationSyntax fd
                && fd.Modifiers.Any(SyntaxKind.ConstKeyword):
                return ($"const {v.Identifier.Text}", "const declaration (no IL)");
            case EqualsValueClauseSyntax { Parent: ParameterSyntax p }:
                return ($"default parameter {p.Identifier.Text}", "default parameter (emitted at callers)");
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v }:
                return ($"init {v.Identifier.Text}", BoundName.IsMatch(v.Identifier.Text) ? "interval end" : "value");
            case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax pd }:
                return ($"init prop {pd.Identifier.Text}", BoundName.IsMatch(pd.Identifier.Text) ? "interval end" : "value");
            case AssignmentExpressionSyntax asg when asg.Right == current:
            {
                string left = asg.Left.ToString();
                return ($"assign {left} {asg.OperatorToken.Text}", asg.Kind() != SyntaxKind.SimpleAssignmentExpression ? "arithmetic operand"
                    : BoundName.IsMatch(left) ? "interval end" : "value");
            }
            case ArrowExpressionClauseSyntax or ReturnStatementSyntax:
            {
                string member = current.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
                {
                    MethodDeclarationSyntax m => m.Identifier.Text,
                    PropertyDeclarationSyntax p => p.Identifier.Text,
                    _ => "",
                };
                return ($"return from {member}", BoundName.IsMatch(member) ? "interval end" : "value");
            }
            case AttributeArgumentSyntax:
                return ("attribute argument", "attribute (no IL)");
            case InitializerExpressionSyntax:
                return ($"initializer: {current.Parent}", "value");
            case { } other:
                return ($"{other.Kind()}: {other}", "value");
            default:
                return ("", "value");
        }
    }

    static string ReinterpretResult(string name, IMethodSymbol method, object? value)
    {
        try
        {
            if (name.EndsWith("BitsToSingle", StringComparison.Ordinal) || method.TypeArguments.LastOrDefault()?.SpecialType == SpecialType.System_Single)
            {
                uint bits = unchecked((uint)Convert.ToInt64(value));
                float f = BitConverter.UInt32BitsToSingle(bits);
                return $"0x{bits:X8} {(f != 0 && MathF.Abs(f) < SmallestNormal ? "SUBNORMAL" : "normal-or-zero")}";
            }
            ulong dbits = unchecked((ulong)Convert.ToInt64(value));
            double d = BitConverter.UInt64BitsToDouble(dbits);
            return $"0x{dbits:X16} {(d != 0 && Math.Abs(d) < SmallestNormalDouble ? "SUBNORMAL" : "normal-or-zero")}";
        }
        catch (Exception) { return "unknown"; }
    }

    static string Describe(object? value) => value switch
    {
        float f => $"{f:R}f bits 0x{BitConverter.SingleToUInt32Bits(f):X8}",
        double d => $"{d:R} bits 0x{BitConverter.DoubleToUInt64Bits(d):X16}",
        null => "null",
        _ => value.ToString() ?? "",
    };

    static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Replace('\t', ' ');
    static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
