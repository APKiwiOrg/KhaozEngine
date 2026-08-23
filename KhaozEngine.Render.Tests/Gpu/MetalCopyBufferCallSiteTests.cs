using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SECTION 9.3's OWN FOLLOW-UP CONDITION, AS A TEST: no <c>IGpuCommandList.CopyBuffer</c> call site in this
    /// repository produces an offset that is not a multiple of four, so nothing legitimate reaches the throw in
    /// <see cref="MetalCopyAlignment.RequireAlignedOffset"/>.
    ///
    /// <para><b>THE THROW IS A DELIBERATE HOLE AND THIS IS WHAT SAYS THE HOLE IS EMPTY.</b> macOS requires both
    /// offsets of <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c> to be multiples of four.
    /// The incumbent routed an unaligned copy through an embedded compute shader driven by a dedicated compute
    /// pipeline, and shipping a second metallib plus a second pipeline for a case no consumer produces is the
    /// unreachable-code reproduction G1 declined once already. That decision is only safe while the refusal stays
    /// unreachable, and "stays" is the word a test has to carry: the day someone adds a call site with a computed
    /// offset, this is what says so, rather than a Mac at runtime.</para>
    ///
    /// <para><b>IT IS MECHANICAL RATHER THAN A LIST, because a list is stale the day someone adds a call
    /// site.</b> The sweep reads the repository's own source at test time, locating it with
    /// <see cref="CallerFilePathAttribute"/>, which is the technique <c>GoldenCompare.GoldenPath</c> and the three
    /// shader byte-equality tables already use and which is independent of <c>dotnet test</c>'s working directory
    /// and the build output layout.</para>
    ///
    /// <para><b>HOW A CALL SITE IS CLASSIFIED</b>, in the order the sweep asks. A FORWARD is an invocation inside
    /// a file that declares the seam member itself, passing that declaration's own offset parameters through
    /// unchanged: a backend's implementation of <c>CopyBuffer</c> handing its arguments to its private emitter
    /// produces no offset of its own and is skipped. Everything else is resolved by RECEIVER: the sweep reads the
    /// declared type of the identifier the invocation is called on out of the same file, and the invocation is a
    /// seam call site when and only when that type is <c>IGpuCommandList</c>. An `X.CopyBuffer(...)` whose
    /// receiver cannot be resolved at all, which includes one declared with <c>var</c>, is a VIOLATION rather
    /// than a skip, because a sweep that quietly drops what it does not understand reports clean for the wrong
    /// reason.</para>
    ///
    /// <para><b>AN OFFSET THAT IS A FORWARDED PARAMETER IS PROVED ONE LEVEL UP RATHER THAN ACCEPTED.</b> The one
    /// shipped call site passes <c>GpuReadback.ReadBuffer</c>'s own <c>srcOffsetBytes</c> through, so the sweep
    /// resolves that parameter's default, requires the default to be aligned, and then sweeps every call of that
    /// method in the repository requiring each to leave it at the default or pass an aligned literal. Accepting a
    /// bare identifier without that second pass would make the whole file vacuous.</para>
    ///
    /// <para><b>WHAT IT DOES NOT SEE, stated so the guarantee is not read as wider than it is.</b> It is a text
    /// sweep, not a compilation: it strips comments but not string literals, it resolves a receiver's type by
    /// looking for a declaration of that identifier in the same FILE, and the caller sweep for a forwarded
    /// parameter matches DOTTED invocations, so a same-class unqualified call would not be seen (there is none:
    /// <c>GpuReadback</c> is a static utility class that calls none of its own members). The test projects are
    /// excluded from the call-site sweep on purpose, because they deliberately drive the refusals. They are NOT
    /// excluded from the forwarded-CALLER pass, because every <c>GpuReadback.ReadBuffer</c> caller in this
    /// repository lives in one, so dropping them would leave that pass with nothing to check: the two files that
    /// drive the refusal deliberately are named in <c>DrivesTheRefusalOnPurpose</c> instead. And the
    /// repository is not the world: <c>GpuReadback.ReadBuffer</c> is PUBLIC API, so a consumer outside this
    /// repository can still reach the refusal by passing an unaligned offset of its own.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either a new call site computes an offset (in which case section 9.3's
    /// follow-up is now live and the unaligned path has to be built or the call site changed), or the sweep found
    /// something it could not classify, or the sweep found NOTHING, which is the failure mode a grep-shaped guard
    /// dies of quietly and which the control rows below exist to catch.</para>
    /// </summary>
    public sealed class MetalCopyBufferCallSiteTests
    {
        readonly ITestOutputHelper _output;

        public MetalCopyBufferCallSiteTests(ITestOutputHelper output) => _output = output;

        // ---- The rows --------------------------------------------------------------------------------------

        /// <summary>
        /// THE RULE. Every seam call site passes a source and a destination offset that
        /// <see cref="MetalCopyAlignment.IsAligned"/> accepts, proved from a literal or from the default of the
        /// parameter it forwards.
        /// </summary>
        [Fact]
        public void EveryCopyBufferCallSite_PassesOffsetsTheAlignmentRuleAccepts()
        {
            foreach (Site site in Result.SeamSites) _output.WriteLine("seam call site: " + Describe(site));

            Assert.True(Result.Violations.Count == 0,
                "A CopyBuffer call site in this repository can produce an offset the native Metal backend "
                + "refuses, or the sweep could not decide whether it does. Section 9.3 declined to reproduce the "
                + "incumbent's unaligned-copy compute shader on the grounds that no shipped call site needs it, "
                + "and this row is that ground. Either align the offset at the call site or build the unaligned "
                + "path.\n" + string.Join("\n", Result.Violations));
        }

        /// <summary>
        /// THE CONTROL FOR THE PRODUCER, and the reason it is a separate row: a sweep that read nothing reports
        /// clean, and a clean report from a dead producer is indistinguishable from a clean repository. This
        /// pins that the root really is this repository and that a realistic number of files came back.
        /// </summary>
        [Fact]
        public void TheSweep_ReadsThisRepositoryRatherThanAnEmptyDirectory()
        {
            _output.WriteLine(Result.Root);
            _output.WriteLine(Result.ShippedFiles.ToString(CultureInfo.InvariantCulture) + " shipped source files");

            Assert.True(File.Exists(Path.Combine(Result.Root, "KhaozEngine.slnx")),
                "The sweep's repository root has no KhaozEngine.slnx in it: " + Result.Root);
            Assert.True(Result.ShippedFiles > 500,
                "The sweep found only " + Result.ShippedFiles.ToString(CultureInfo.InvariantCulture)
                + " shipped .cs files, which is far too few for this repository. The producer is broken rather "
                + "than the repository being clean.");
        }

        /// <summary>
        /// AND THE CONTROL FOR THE MATCHER: the sweep finds the ONE shipped seam call site it was written for,
        /// <c>GpuReadback.ReadBuffer</c>'s copy into its staging buffer. Without this the rule above would pass
        /// on a regex that matched nothing at all.
        /// </summary>
        [Fact]
        public void TheSweep_FindsTheSeamCallSiteItWasWrittenFor()
        {
            Assert.NotEmpty(Result.SeamSites);
            Assert.Contains(Result.SeamSites,
                site => site.File.Replace('\\', '/').EndsWith("KhaozEngine.Gpu/GpuReadback.cs",
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// AND THE CONTROL FOR THE CLASSIFIER: every <c>CopyBuffer</c> invocation the sweep found was accounted
        /// for as a seam call site, a forward, or an invocation on some other interface whose receiver type it
        /// actually read. An unclassified one is already a violation of the rule above, and this row says the
        /// classifier is doing work rather than finding one invocation and stopping.
        /// </summary>
        [Fact]
        public void TheSweep_ClassifiesEveryCopyBufferInvocationItFinds()
        {
            _output.WriteLine(Result.Invocations.ToString(CultureInfo.InvariantCulture) + " invocations, "
                + Result.SeamSites.Count.ToString(CultureInfo.InvariantCulture) + " seam, "
                + Result.Forwards.Count.ToString(CultureInfo.InvariantCulture) + " forwards, "
                + Result.Other.Count.ToString(CultureInfo.InvariantCulture) + " on another interface");

            Assert.True(Result.Invocations >= 5,
                "The sweep found " + Result.Invocations.ToString(CultureInfo.InvariantCulture)
                + " CopyBuffer invocations in shipped source, which is fewer than the backends alone contain.");
            Assert.Equal(Result.Invocations,
                Result.SeamSites.Count + Result.Forwards.Count + Result.Other.Count);
        }

        /// <summary>
        /// AND THE CONTROL FOR THE SECOND PASS: the forwarded-parameter arm really did find callers to check.
        /// The one forwarded offset in the repository belongs to a public helper with several callers, so a zero
        /// here means the caller sweep matched nothing and the alignment proof rests on a default nobody was
        /// shown to use.
        /// </summary>
        [Fact]
        public void TheForwardedOffsetArm_FoundCallersToCheck()
        {
            _output.WriteLine(Result.ForwardedOffsets.ToString(CultureInfo.InvariantCulture)
                + " forwarded offsets, " + Result.ForwardedCallers.ToString(CultureInfo.InvariantCulture)
                + " callers checked");

            Assert.True(Result.ForwardedOffsets == 0 || Result.ForwardedCallers > 0,
                "The sweep resolved a forwarded offset parameter and then found no call of the method that "
                + "carries it, which means the caller sweep is broken rather than that nobody overrides the "
                + "default.");
        }

        // ---- The sweep -------------------------------------------------------------------------------------

        /// <summary>One invocation, as the sweep read it.</summary>
        /// <param name="File">Absolute path of the source file.</param>
        /// <param name="Line">One-based line number, for a report a human can act on.</param>
        /// <param name="Receiver">The identifier the member was invoked on.</param>
        /// <param name="Arguments">The top-level arguments, in order, as written.</param>
        internal sealed record Site(string File, int Line, string Receiver, IReadOnlyList<string> Arguments);

        /// <summary>Everything one sweep produced, computed once and read by every row.</summary>
        internal sealed record Analysis(string Root, int ShippedFiles, int Invocations,
            IReadOnlyList<Site> SeamSites, IReadOnlyList<Site> Forwards, IReadOnlyList<Site> Other,
            int ForwardedOffsets, int ForwardedCallers, IReadOnlyList<string> Violations);

        static readonly Analysis Result = Analyse();

        // The seam's own five-parameter shape. An invocation with a different arity is a different member on a
        // different interface, and the backends have several.
        const int SeamArity = 5;
        const int SourceOffsetArgument = 1;
        const int DestinationOffsetArgument = 3;

        const string InvocationPattern = @"\b(?<receiver>[A-Za-z_]\w*)\s*\.\s*CopyBuffer\s*\(";

        // A method-signature shape: a name, optional type parameters, and a parameter list with no nested
        // parentheses. Used to find the declaration that OWNS a forwarded parameter, which is the last such match
        // before the call site whose parameter list actually declares that identifier.
        const string SignaturePattern = @"\b(?<name>[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\((?<params>[^()]*)\)";

        // The seam member's own declaration, which is what makes a file a backend implementation rather than a
        // caller.
        const string SeamDeclarationPattern = @"\bCopyBuffer\s*\(\s*(?<params>[^()]*)\)";

        static readonly string[] NotTypeKeywords =
        [
            "var", "return", "new", "is", "as", "in", "out", "ref", "params", "this", "base", "readonly",
            "static", "public", "private", "protected", "internal", "const", "sealed", "partial", "abstract",
            "virtual", "override", "async", "await", "throw", "else", "using", "case", "yield", "when", "lock",
            "fixed", "unsafe", "extern", "volatile", "event", "delegate", "checked", "unchecked", "default",
            "typeof", "nameof", "sizeof", "stackalloc", "do", "while", "for", "foreach", "if", "switch", "goto",
        ];

        static Analysis Analyse()
        {
            string root = RepositoryRoot();
            List<(string Path, string Text)> all = Sources(root).ToList();
            List<(string Path, string Text)> shipped = all.Where(file => !IsTestProject(root, file.Path)).ToList();

            var violations = new List<string>();
            var seam = new List<Site>();
            var forwards = new List<Site>();
            var other = new List<Site>();
            int invocations = 0;
            int forwardedOffsets = 0;
            int forwardedCallers = 0;

            foreach ((string path, string text) in shipped)
            {
                string relative = Path.GetRelativePath(root, path);

                foreach (Match match in Regex.Matches(text, InvocationPattern))
                {
                    invocations++;

                    int open = match.Index + match.Length - 1;
                    List<string>? arguments = Arguments(text, open);
                    if (arguments is null)
                    {
                        violations.Add(relative + ":" + Line(text, match.Index)
                            + " has a CopyBuffer argument list the sweep could not read to its closing bracket.");
                        other.Add(new Site(path, Line(text, match.Index), match.Groups["receiver"].Value, []));
                        continue;
                    }

                    var site = new Site(path, Line(text, match.Index), match.Groups["receiver"].Value, arguments);

                    if (IsSeamForward(text, arguments))
                    {
                        forwards.Add(site);
                        continue;
                    }

                    if (!IsSeamReceiver(text, site.Receiver, relative, site.Line, violations))
                    {
                        other.Add(site);
                        continue;
                    }

                    seam.Add(site);
                    CheckSite(site, text, match.Index, relative, root, all, violations, ref forwardedOffsets,
                        ref forwardedCallers);
                }
            }

            return new Analysis(root, shipped.Count, invocations, seam, forwards, other, forwardedOffsets,
                forwardedCallers, violations);
        }

        // BOTH OFFSETS OF ONE SEAM CALL SITE. A literal settles it outright, a bare identifier is a forwarded
        // parameter and is settled one level up, and anything else is an expression the sweep cannot evaluate,
        // which is exactly the case section 9.3's follow-up exists for.
        static void CheckSite(Site site, string text, int index, string relative, string root,
            IReadOnlyList<(string Path, string Text)> all, List<string> violations, ref int forwardedOffsets,
            ref int forwardedCallers)
        {
            if (site.Arguments.Count != SeamArity)
            {
                violations.Add(Describe(site) + " calls CopyBuffer on an IGpuCommandList with "
                    + site.Arguments.Count.ToString(CultureInfo.InvariantCulture)
                    + " arguments, and the seam member takes " + SeamArity.ToString(CultureInfo.InvariantCulture)
                    + ". The sweep cannot tell which of them are the offsets.");
                return;
            }

            foreach ((int position, string side) in
                new[] { (SourceOffsetArgument, "source"), (DestinationOffsetArgument, "destination") })
            {
                string argument = site.Arguments[position];

                if (IsAlignedLiteral(argument)) continue;

                if (!Regex.IsMatch(argument, @"^[A-Za-z_]\w*$"))
                {
                    string multiple = MetalCopyAlignment.Bytes.ToString(CultureInfo.InvariantCulture);

                    violations.Add(Describe(site) + " passes the " + side + " offset as '" + argument
                        + "', which is " + (IsIntegerLiteral(argument)
                            ? "a literal and not a multiple of " + multiple + "."
                            : "neither a literal nor a parameter the sweep can follow, so it could be anything, "
                                + "and section 9.3's ruling needs a multiple of " + multiple + "."));
                    continue;
                }

                forwardedOffsets++;
                forwardedCallers += CheckForwardedOffset(site, argument, side, text, index, relative, root, all,
                    violations);
            }
        }

        // A FORWARDED OFFSET, PROVED ONE LEVEL UP. Resolve the declaration that owns the parameter, require its
        // default to be aligned, then require every call of that method in the repository to leave it at the
        // default or pass an aligned literal. Returns how many callers were checked, which the control row reads.
        static int CheckForwardedOffset(Site site, string parameter, string side, string text, int index,
            string relative, string root, IReadOnlyList<(string Path, string Text)> all, List<string> violations)
        {
            Match? owner = null;
            foreach (Match candidate in Regex.Matches(text, SignaturePattern))
            {
                if (candidate.Index >= index) break;
                if (ParameterIndex(candidate.Groups["params"].Value, parameter) >= 0) owner = candidate;
            }

            if (owner is null)
            {
                violations.Add(Describe(site) + " passes the " + side + " offset as the parameter '" + parameter
                    + "', and the sweep found no declaration in " + relative + " that owns it, so it cannot say "
                    + "what values reach it.");
                return 0;
            }

            string name = owner.Groups["name"].Value;
            string parameters = owner.Groups["params"].Value;
            int position = ParameterIndex(parameters, parameter);
            string? fallback = ParameterDefault(parameters, position);

            if (fallback is null || !IsAlignedLiteral(fallback))
            {
                violations.Add(Describe(site) + " forwards " + name + "'s '" + parameter + "' as its " + side
                    + " offset, and that parameter has "
                    + (fallback is null ? "no default at all" : "the default '" + fallback + "'")
                    + ". An unaligned or unknown default is a caller away from the refusal.");
                return 0;
            }

            int callers = 0;
            string pattern = @"\.\s*" + Regex.Escape(name) + @"\b\s*(?:<[^<>]*>)?\s*\(";

            foreach ((string path, string caller) in all)
            {
                if (DrivesTheRefusalOnPurpose(path)) continue;

                foreach (Match call in Regex.Matches(caller, pattern))
                {
                    List<string>? arguments = Arguments(caller, call.Index + call.Length - 1);
                    if (arguments is null) continue;

                    callers++;
                    if (arguments.Count <= position) continue;

                    string argument = Named(arguments[position]);
                    if (IsAlignedLiteral(argument)) continue;

                    violations.Add(Path.GetRelativePath(root, path) + ":"
                        + Line(caller, call.Index).ToString(CultureInfo.InvariantCulture) + " calls " + name
                        + " with '" + argument + "' for '" + parameter + "', which reaches the native Metal "
                        + "CopyBuffer as its " + side + " offset and is not a literal multiple of "
                        + MetalCopyAlignment.Bytes.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }

            return callers;
        }

        // ---- Reading the source ----------------------------------------------------------------------------

        // THE FILE'S OWN SEAM DECLARATION, forwarded through. A backend's CopyBuffer handing its own offset
        // parameters to a private emitter originates nothing, and the proof is that the two offset arguments ARE
        // the declaration's own parameter names at the same positions.
        static bool IsSeamForward(string text, IReadOnlyList<string> arguments)
        {
            if (arguments.Count != SeamArity) return false;

            foreach (Match declaration in Regex.Matches(text, SeamDeclarationPattern))
            {
                string parameters = declaration.Groups["params"].Value;
                if (!parameters.TrimStart().StartsWith("IGpuBuffer", StringComparison.Ordinal)) continue;

                if (ParameterIndex(parameters, arguments[SourceOffsetArgument]) == SourceOffsetArgument
                    && ParameterIndex(parameters, arguments[DestinationOffsetArgument])
                        == DestinationOffsetArgument)
                {
                    return true;
                }
            }

            return false;
        }

        // THE RECEIVER'S DECLARED TYPE, out of the same file. An identifier nothing declares, or one declared
        // with var, is recorded as a violation rather than skipped: a sweep that drops what it cannot read
        // reports clean for the wrong reason.
        static bool IsSeamReceiver(string text, string receiver, string relative, int line,
            List<string> violations)
        {
            string pattern = @"\b(?<type>[A-Za-z_][\w.]*(?:<[^<>]*>)?\??)\s+" + Regex.Escape(receiver) + @"\b";

            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(text, pattern))
            {
                string type = match.Groups["type"].Value;
                if (!NotTypeKeywords.Contains(type)) types.Add(type);
            }

            if (types.Count == 0)
            {
                violations.Add(relative + ":" + line.ToString(CultureInfo.InvariantCulture)
                    + " calls CopyBuffer on '" + receiver + "', and the sweep found no declaration of that "
                    + "identifier in the file, so it cannot say whether the call reaches IGpuCommandList. An "
                    + "implicitly typed receiver reads this way too.");
                return false;
            }

            return types.Contains("IGpuCommandList");
        }

        // Every .cs file under the root that is not build output. Read once, with comments blanked in place so
        // indices, and therefore line numbers, still point at the original text.
        static IEnumerable<(string Path, string Text)> Sources(string root)
            => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsExcluded(root, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (path, WithoutComments(File.ReadAllText(path))));

        static bool IsExcluded(string root, string path)
            => Segments(root, path).Any(segment =>
                segment is "obj" or "bin" or "local-feed" or ".git" or ".claude" or ".buildhome" or "vendor"
                    or "artifacts");

        // THE FILES WHOSE WHOLE JOB IS TO PASS AN OFFSET THE SEAM REFUSES, so the caller pass reads them as
        // intent rather than as a violation (17.40.0, https://github.com/APKiwiOrg/KhaozEngine/issues/684).
        // They are named one by one rather than excluded as a class, because every other ReadBuffer caller in
        // the repository is also in a test project and those are exactly what this pass exists to check. Since
        // #684 the refusal is the SEAM's contract on all four backends rather than the Metal backend's alone,
        // and a contract with no test that trips it is a contract nobody has run.
        // A pattern match rather than a static array, because the sweep itself runs from a static field
        // initializer declared further up this file and would read an array that has not been assigned yet.
        static bool DrivesTheRefusalOnPurpose(string path)
            => Path.GetFileName(path) is "CopyBufferOffsetContractTests.cs" or "CopyBufferOffsetGpuTests.cs";

        static bool IsTestProject(string root, string path)
            => Segments(root, path).Any(segment => segment.EndsWith("Tests", StringComparison.Ordinal));

        static string[] Segments(string root, string path)
            => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // COMMENTS BLANKED IN PLACE, length and newlines preserved. String literals are deliberately left alone:
        // nothing in this repository writes a receiver, a dot and the member name inside one, and a blanker that
        // tried to track verbatim, interpolated and raw strings could desync and hide a real call site, which is
        // the failure direction that matters.
        static string WithoutComments(string text)
        {
            char[] chars = text.ToCharArray();

            for (int i = 0; i < chars.Length - 1; i++)
            {
                if (chars[i] != '/') continue;

                if (chars[i + 1] == '/')
                {
                    while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
                }
                else if (chars[i + 1] == '*')
                {
                    while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                    {
                        if (chars[i] != '\n') chars[i] = ' ';
                        i++;
                    }

                    if (i < chars.Length) chars[i] = ' ';
                    if (i + 1 < chars.Length) chars[i + 1] = ' ';
                    i++;
                }
            }

            return new string(chars);
        }

        // ---- Small parsers ---------------------------------------------------------------------------------

        // The top-level arguments of the list opening at openIndex, or null when the brackets do not balance.
        static List<string>? Arguments(string text, int openIndex)
        {
            var arguments = new List<string>();
            int depth = 0;
            int start = openIndex + 1;
            char quote = '\0';

            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];

                if (quote != '\0')
                {
                    if (c == '\\') i++;
                    else if (c == quote) quote = '\0';
                    continue;
                }

                switch (c)
                {
                    case '"':
                    case '\'':
                        quote = c;
                        break;

                    case '(':
                    case '[':
                        depth++;
                        break;

                    case ')':
                    case ']':
                        depth--;
                        if (depth != 0) break;

                        // The last argument, unless the list was empty, which is what an argument-free call
                        // looks like and is not the same thing as one empty argument.
                        string last = text[start..i].Trim();
                        if (last.Length > 0 || arguments.Count > 0) arguments.Add(last);
                        return arguments;

                    case ',' when depth == 1:
                        arguments.Add(text[start..i].Trim());
                        start = i + 1;
                        break;

                    default:
                        break;
                }
            }

            return null;
        }

        // Which position parameter is at in a declaration's parameter list, or -1. Commas inside type arguments
        // do not split a parameter, which is why this is not a plain Split.
        static int ParameterIndex(string parameters, string parameter)
        {
            List<string> parts = SplitParameters(parameters);

            for (int i = 0; i < parts.Count; i++)
            {
                if (ParameterName(parts[i]) == parameter) return i;
            }

            return -1;
        }

        static string? ParameterDefault(string parameters, int position)
        {
            List<string> parts = SplitParameters(parameters);
            if (position < 0 || position >= parts.Count) return null;

            int equals = parts[position].IndexOf('=', StringComparison.Ordinal);
            return equals < 0 ? null : parts[position][(equals + 1)..].Trim();
        }

        static List<string> SplitParameters(string parameters)
        {
            var parts = new List<string>();
            int depth = 0;
            int start = 0;

            for (int i = 0; i < parameters.Length; i++)
            {
                char c = parameters[i];
                if (c is '<' or '[') depth++;
                else if (c is '>' or ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(parameters[start..i]);
                    start = i + 1;
                }
            }

            if (parameters[start..].Trim().Length > 0) parts.Add(parameters[start..]);
            return parts;
        }

        // The declared name of one parameter: the last identifier before any default value.
        static string ParameterName(string part)
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string declaration = (equals < 0 ? part : part[..equals]).Trim();

            string[] words = declaration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return words.Length < 2 ? string.Empty : words[^1];
        }

        // A named argument's value, or the argument unchanged. `srcOffsetBytes: 8` is the same claim as `8`.
        static string Named(string argument)
        {
            Match match = Regex.Match(argument, @"^[A-Za-z_]\w*\s*:\s*(?<value>.+)$", RegexOptions.Singleline);
            return match.Success ? match.Groups["value"].Value.Trim() : argument;
        }

        // AN INTEGER LITERAL THE COPY SELECTOR WOULD ACCEPT, read against the production constant rather than
        // against a 4 typed here, so the two cannot drift.
        static bool IsAlignedLiteral(string argument)
            => IntegerLiteral(argument) is ulong value && MetalCopyAlignment.IsAligned(value);

        // Whether it is an integer literal at all, which is what separates "someone wrote 3" from "someone wrote
        // an expression". The two need different sentences in the report and different fixes.
        static bool IsIntegerLiteral(string argument) => IntegerLiteral(argument) is not null;

        static ulong? IntegerLiteral(string argument)
        {
            string text = argument.Trim();
            while (text.Length > 0 && text[^1] is 'u' or 'U' or 'l' or 'L') text = text[..^1];
            text = text.Replace("_", string.Empty, StringComparison.Ordinal);

            bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            string digits = hex ? text[2..] : text;
            NumberStyles styles = hex ? NumberStyles.HexNumber : NumberStyles.None;

            return ulong.TryParse(digits, styles, CultureInfo.InvariantCulture, out ulong value) ? value : null;
        }

        static int Line(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

        static string Describe(Site site) => site.File + ":"
            + site.Line.ToString(CultureInfo.InvariantCulture) + " (" + site.Receiver + ".CopyBuffer)";

        // THE REPOSITORY ROOT, found by walking up from this source file to the solution. Located through
        // CallerFilePath rather than the working directory, which is the technique GoldenCompare and the three
        // shader byte-equality tables already use.
        static string RepositoryRoot([CallerFilePath] string thisFile = "")
        {
            DirectoryInfo? directory = new FileInfo(thisFile).Directory;

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "KhaozEngine.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "The CopyBuffer call-site sweep could not find KhaozEngine.slnx above " + thisFile
                + ". It reads the repository's own source at test time, so it needs the checked-out tree the "
                + "assembly was compiled from.");
        }
    }
}
