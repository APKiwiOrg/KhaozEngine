using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// The backstop every bug report string passes through before it leaves the machine. Masks private key
/// blocks, JWTs, well-known provider tokens, Bearer and Basic credentials, secret URL query values and secret
/// key and value pairs with <see cref="Mask"/>, then any supplied reporter identity terms, then replaces the user's
/// home directory with <c>~</c>. Rules run in that fixed order. Every secret and identity rule runs on the
/// non-backtracking engine, so it is linear in the input
/// by construction, and the home rule is an escaped literal path. Every pattern also has a 250 ms match timeout
/// as a backstop, and a timeout fails closed, so a string the rules cannot finish with comes back as
/// <see cref="Mask"/> whole. Thread-safe, and a string with nothing to mask is returned as the same instance
/// without allocating.
/// </summary>
public sealed class ReportRedactor
{
    /// <summary>What every masked secret, and a string that timed out, is replaced with.</summary>
    public const string Mask = "[redacted]";

    /// <summary>A home path shorter than this is not replaced, since it would match almost any path.</summary>
    const int MinimumHomeLength = 3;

    const RegexOptions Options = RegexOptions.CultureInvariant;

    /// <summary>The secret rules' options. The engine scans in time linear in the input whatever the pattern.</summary>
    const RegexOptions RuleOptions = Options | RegexOptions.NonBacktracking;

    static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Variable name fragments whose whole value is masked whatever it holds.</summary>
    static readonly string[] SecretNameParts = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "KEY"];

    static readonly Rule[] DefaultRules = BuildRules(DefaultMatchTimeout);

    readonly Rule[] rules;
    readonly Regex? home;
    readonly TimeSpan matchTimeout;
    readonly ReporterIdentityRedaction? identity;

    /// <summary>
    /// Creates a redactor that replaces <paramref name="homeDirectory"/> with <c>~</c>. Null reads the user
    /// profile folder. Trailing separators are ignored, and a home shorter than three characters after that
    /// (empty, <c>/</c>, a bare drive) turns the home rule off.
    /// </summary>
    public ReportRedactor(string? homeDirectory = null)
        : this(homeDirectory, DefaultRules, DefaultMatchTimeout)
    {
    }

    /// <summary>A redactor whose rules use <paramref name="matchTimeout"/>, so a test can force a timeout or turn it off.</summary>
    internal ReportRedactor(string? homeDirectory, TimeSpan matchTimeout)
        : this(homeDirectory, BuildRules(matchTimeout), matchTimeout)
    {
    }

    ReportRedactor(string? homeDirectory, Rule[] rules, TimeSpan matchTimeout)
    {
        this.rules = rules;
        this.matchTimeout = matchTimeout;
        string path = (homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd('/', '\\');
        if (path.Length >= MinimumHomeLength) home = BuildHomePattern(path, matchTimeout);
    }

    ReportRedactor(ReportRedactor source, ReporterIdentityRedaction identity)
    {
        rules = source.rules;
        home = source.home;
        matchTimeout = source.matchTimeout;
        this.identity = identity;
    }

    /// <summary>
    /// Returns an independent redactor with a snapshot of <paramref name="reporterIdentityTerms"/>, called once
    /// now. Each term is an escaped literal, matched case-insensitively with invariant culture, longest first,
    /// after secrets and before home. Null, blank and shorter-than-three-character terms are ignored. Matches
    /// become <see cref="Mask"/>. The non-backtracking matcher uses this redactor's timeout. If the source throws,
    /// returns null, or the combined pattern is unsupported or exceeds 4096 escaped characters including separators,
    /// every string is masked. The source must finish promptly. This redactor is unchanged.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reporterIdentityTerms"/> is null.</exception>
    public ReportRedactor WithReporterIdentityTerms(Func<IEnumerable<string>> reporterIdentityTerms)
    {
        ArgumentNullException.ThrowIfNull(reporterIdentityTerms);
        return new ReportRedactor(this, ReporterIdentityRedaction.Capture(reporterIdentityTerms, matchTimeout));
    }

    /// <summary>The secret rules in the order they run, for tests that check how they are built.</summary>
    internal IReadOnlyList<Rule> Rules => rules;

    /// <summary>The home rule, or null when the home is too short to replace.</summary>
    internal Regex? HomePattern => home;

    /// <summary>
    /// <paramref name="text"/> with every rule applied in order. Returns the same instance when nothing
    /// matched, and <see cref="Mask"/> when any rule timed out.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public string Redact(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length == 0) return text;

        // A rule replaces only after it has matched. A regex holds its parsed replacement only weakly, so a
        // replace on clean text after a full collection would parse it again and allocate.
        try
        {
            string result = text;
            foreach (Rule rule in rules)
            {
                if (rule.Pattern.IsMatch(result))
                    result = rule.Pattern.Replace(result, rule.Replacement);
            }
            if (identity is not null) result = identity.Redact(result);
            if (home is not null && home.IsMatch(result))
                result = home.Replace(result, "~");
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }
    }

    /// <summary>
    /// The <c>KE_</c> entries of <paramref name="variables"/>, chosen and sorted exactly as
    /// <see cref="TelemetrySessionHeader.SelectEngineVariables"/> does, with every value passed through
    /// <see cref="Redact"/>. A name containing <c>TOKEN</c>, <c>SECRET</c>, <c>PASSWORD</c>, <c>PASSWD</c> or
    /// <c>KEY</c> in any case has its whole value masked. Nothing without the prefix reaches a report.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is null.</exception>
    public IReadOnlyList<TelemetryHeaderValue> SelectEnvironment(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        IReadOnlyList<TelemetryHeaderValue> selected = TelemetrySessionHeader.SelectEngineVariables(variables);
        var redacted = new TelemetryHeaderValue[selected.Count];
        for (int i = 0; i < redacted.Length; i++)
        {
            string name = selected[i].Key;
            string value = IsSecretName(name) ? Mask : Redact(selected[i].Value);
            redacted[i] = new TelemetryHeaderValue(name, value);
        }
        return Array.AsReadOnly(redacted);
    }

    static bool IsSecretName(string name)
    {
        foreach (string part in SecretNameParts)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static Rule[] BuildRules(TimeSpan timeout) =>
    [
        // PEM private key block.
        new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[A-Za-z0-9+/=\s]*-----END [A-Z0-9 ]*PRIVATE KEY-----", Mask, timeout),
        // JWT, only at the start of a token run: the start of the text or after a character that cannot be in a
        // token, kept by the replacement. The non-backtracking engine has no look-behind, so the prefix is captured.
        new(@"(^|[^A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", "$1" + Mask, timeout),
        // GitHub, Slack, AWS access key id and OpenAI style tokens.
        new(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,})\b", Mask, timeout),
        // Bearer and Basic credentials keep the scheme and its spacing.
        new(@"(?i)\b(bearer|basic)(\s+)[A-Za-z0-9._~+/=-]{8,}", "$1$2" + Mask, timeout),
        // Secret URL query values keep the parameter name.
        new(@"(?i)([?&](?:sig|signature|code|token|access_token|id_token|refresh_token|key|apikey|api_key|client_secret|password)=)[^&#\s""']+", "$1" + Mask, timeout),
        // Secret key and value pairs keep the key, its quotes and its separator.
        new(@"(?i)((?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|connection[_-]?string|accountkey|sharedaccesskey)[""']?\s*[:=]\s*[""']?)[^\s""',;&]+", "$1" + Mask, timeout),
    ];

    /// <summary>
    /// The home path as written and with its separators swapped, case-insensitive, and only where the path
    /// ends there, so a sibling such as <c>/Users/anna</c> is left alone for a home of <c>/Users/ann</c>.
    /// </summary>
    static Regex BuildHomePattern(string path, TimeSpan timeout)
    {
        // Backtracking for the look-ahead, and linear anyway because the body is an escaped literal path.
        string native = Regex.Escape(path);
        string alternate = Regex.Escape(SwapSeparators(path));
        string body = native == alternate ? native : "(?:" + native + "|" + alternate + ")";
        return new Regex(body + @"(?=[/\\]|$|[^A-Za-z0-9._-])", Options | RegexOptions.IgnoreCase, timeout);
    }

    static string SwapSeparators(string path)
    {
        char[] chars = path.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '/') chars[i] = '\\';
            else if (chars[i] == '\\') chars[i] = '/';
        }
        return new string(chars);
    }

    internal readonly struct Rule(string pattern, string replacement, TimeSpan timeout)
    {
        public Regex Pattern { get; } = new(pattern, RuleOptions, timeout);

        public string Replacement { get; } = replacement;
    }
}
