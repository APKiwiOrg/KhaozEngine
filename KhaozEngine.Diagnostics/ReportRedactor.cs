using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// The backstop every bug report string passes through before it leaves the machine. Masks private key
/// blocks, JWTs, well-known provider tokens, Bearer and Basic credentials, secret URL query values and secret
/// key and value pairs with <see cref="Mask"/>, then replaces the user's home directory with <c>~</c>. Rules
/// run in that fixed order. Every pattern has a 250 ms match timeout and a timeout fails closed, so a string
/// the rules cannot finish with comes back as <see cref="Mask"/> whole. Thread-safe, and a string with
/// nothing to mask is returned as the same instance without allocating.
/// </summary>
public sealed class ReportRedactor
{
    /// <summary>What every masked secret, and a string that timed out, is replaced with.</summary>
    public const string Mask = "[redacted]";

    /// <summary>A home path shorter than this is not replaced, since it would match almost any path.</summary>
    const int MinimumHomeLength = 3;

    const RegexOptions Options = RegexOptions.CultureInvariant;

    static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    static readonly Rule[] Rules =
    [
        // PEM private key block.
        new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[A-Za-z0-9+/=\s]*-----END [A-Z0-9 ]*PRIVATE KEY-----", Mask),
        // JWT.
        new(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", Mask),
        // GitHub, Slack, AWS access key id and OpenAI style tokens.
        new(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,})\b", Mask),
        // Bearer and Basic credentials keep the scheme and its spacing.
        new(@"(?i)\b(bearer|basic)(\s+)[A-Za-z0-9._~+/=-]{8,}", "$1$2" + Mask),
        // Secret URL query values keep the parameter name.
        new(@"(?i)([?&](?:sig|signature|code|token|access_token|id_token|refresh_token|key|apikey|api_key|client_secret|password)=)[^&#\s""']+", "$1" + Mask),
        // Secret key and value pairs keep the key, its quotes and its separator.
        new(@"(?i)((?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|connection[_-]?string|accountkey|sharedaccesskey)[""']?\s*[:=]\s*[""']?)[^\s""',;&]+", "$1" + Mask),
    ];

    readonly Regex? home;

    /// <summary>
    /// Creates a redactor that replaces <paramref name="homeDirectory"/> with <c>~</c>. Null reads the user
    /// profile folder. Trailing separators are ignored, and a home shorter than three characters after that
    /// (empty, <c>/</c>, a bare drive) turns the home rule off.
    /// </summary>
    public ReportRedactor(string? homeDirectory = null)
    {
        string path = (homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd('/', '\\');
        if (path.Length >= MinimumHomeLength) home = BuildHomePattern(path);
    }

    /// <summary>
    /// <paramref name="text"/> with every rule applied in order. Returns the same instance when nothing
    /// matched, and <see cref="Mask"/> when any rule timed out.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public string Redact(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length == 0) return text;

        try
        {
            string result = text;
            foreach (Rule rule in Rules)
                result = rule.Pattern.Replace(result, rule.Replacement);
            if (home is not null)
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
    /// <see cref="Redact"/>. Nothing without the prefix reaches a report.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is null.</exception>
    public IReadOnlyList<TelemetryHeaderValue> SelectEnvironment(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        IReadOnlyList<TelemetryHeaderValue> selected = TelemetrySessionHeader.SelectEngineVariables(variables);
        var redacted = new TelemetryHeaderValue[selected.Count];
        for (int i = 0; i < redacted.Length; i++)
            redacted[i] = new TelemetryHeaderValue(selected[i].Key, Redact(selected[i].Value));
        return Array.AsReadOnly(redacted);
    }

    /// <summary>
    /// The home path as written and with its separators swapped, case-insensitive, and only where the path
    /// ends there, so a sibling such as <c>/Users/anna</c> is left alone for a home of <c>/Users/ann</c>.
    /// </summary>
    static Regex BuildHomePattern(string path)
    {
        string native = Regex.Escape(path);
        string alternate = Regex.Escape(SwapSeparators(path));
        string body = native == alternate ? native : "(?:" + native + "|" + alternate + ")";
        return new Regex(body + @"(?=[/\\]|$|[^A-Za-z0-9._-])", Options | RegexOptions.IgnoreCase, MatchTimeout);
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

    readonly struct Rule(string pattern, string replacement)
    {
        public Regex Pattern { get; } = new(pattern, Options, MatchTimeout);

        public string Replacement { get; } = replacement;
    }
}
