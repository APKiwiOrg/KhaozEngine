using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Catalog;

/// <summary>
/// The next catalog down: the game's own shipped strings, asked when content has none for a key. It is the
/// same shape as <c>IStringCatalog.TryGet</c> in <c>KhaozEngine.App</c>, so a game passes that method group
/// straight in and needs no adapter type of its own.
/// </summary>
/// <param name="key">The lookup key.</param>
/// <param name="value">The value when present, otherwise anything: the caller reads it only on true.</param>
/// <returns>True when the key resolved.</returns>
public delegate bool ContentStringFallback(string key, out string value);

/// <summary>
/// Content strings, LAYERED over the game's shipped catalog (contracts 12.4, spec 7.6). Content is asked
/// FIRST, then the game's catalog, then the standard behaviour of returning THE KEY ITSELF as a visible
/// non-fatal placeholder.
/// <para>
/// Content first, because content is the thing that ships without a client release, so a content string
/// must be able to override a stale shipped one. A miss never throws at any layer: a missing string is a
/// visible defect rather than a dead frame loop.
/// </para>
/// <para>
/// <b>This type does NOT implement <c>IStringCatalog</c> and does not reference <c>KhaozEngine.App</c>.</b>
/// The interface lives in <c>App</c>, which is a peer <c>Foundation</c> package rather than a dependency of
/// this one, and taking a reference on it would widen every game client's graph for one interface. The
/// member shape is the same (<see cref="Get"/>, <see cref="Format"/>, <see cref="TryGet"/>), so the game
/// side adapts it in one small class:
/// </para>
/// <code>
/// sealed class LayeredCatalog(ContentStringCatalog content) : IStringCatalog
/// {
///     public string Get(string key)                        => Select(content).Get(key);
///     public string Format(string key, params object?[] a) => Select(content).Format(key, a);
///     public bool TryGet(string key, out string value)     => Select(content).TryGet(key, out value);
///
///     static ContentStringCatalog Select(ContentStringCatalog content)
///     {
///         content.SelectLanguage(CultureInfo.CurrentUICulture);   // the ambient read lives HERE
///         return content;
///     }
/// }
/// </code>
/// <para>
/// The ambient culture is read on the game's side of that adapter on purpose. This type holds an explicit
/// <see cref="CurrentLanguage"/> and reads no thread state at all, so two tests, two screens or two players
/// in one process cannot move each other's language, and a test never has to write a process-wide culture
/// to exercise a language fallback.
/// </para>
/// </summary>
public sealed class ContentStringCatalog
{
    /// <summary>
    /// The resolved-string cache, DIRECT MAPPED and a power of two. It is bounded by CONSTRUCTION rather
    /// than by a policy, so it cannot grow into the thing budget P10 exists to prevent: at a 60 character
    /// value it is about 80 KB of strings, three ten-thousandths of that budget, and it holds the working
    /// set of a UI screen.
    /// </summary>
    public const int CacheEntries = 512;

    readonly ContentTextIndex[] indexes;
    readonly string[] tags;
    readonly int[][] shardsByTag;
    readonly ContentStringFallback? fallback;
    readonly string?[] cacheValues = new string?[CacheEntries];
    readonly long[] cacheTags = new long[CacheEntries];
    readonly int fallbackLanguage;
    int selected;

    /// <summary>Builds the layered catalog over the languages a client decoded.</summary>
    /// <param name="languages">
    /// One entry per decoded language. Two entries carrying the same tag are SHARDS of that language, which
    /// is how spec 7.6 says a language past the chunk ceiling is held.
    /// </param>
    /// <param name="defaultLanguageTag">
    /// The language a miss in the selected one falls back to, which is the language the content was authored
    /// in. It must be one of <paramref name="languages"/>.
    /// </param>
    /// <param name="shippedCatalog">The game's own catalog, asked when content has no value, or null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="languages"/> or <paramref name="defaultLanguageTag"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="languages"/> is empty, carries a null, or does not carry the default tag.</exception>
    public ContentStringCatalog(
        IReadOnlyList<ContentTextIndex> languages,
        string defaultLanguageTag,
        ContentStringFallback? shippedCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(defaultLanguageTag);
        if (languages.Count == 0)
        {
            throw new ArgumentException("A content string catalog needs at least one language.", nameof(languages));
        }

        indexes = new ContentTextIndex[languages.Count];
        var distinct = new List<string>(languages.Count);
        for (int i = 0; i < languages.Count; i++)
        {
            indexes[i] = languages[i]
                ?? throw new ArgumentException("A language is null.", nameof(languages));
            if (IndexOfTag(distinct, indexes[i].LanguageTag) < 0)
            {
                distinct.Add(indexes[i].LanguageTag);
            }
        }

        tags = [.. distinct];
        shardsByTag = new int[tags.Length][];
        for (int t = 0; t < tags.Length; t++)
        {
            var shards = new List<int>(1);
            for (int i = 0; i < indexes.Length; i++)
            {
                if (string.Equals(indexes[i].LanguageTag, tags[t], StringComparison.OrdinalIgnoreCase))
                {
                    shards.Add(i);
                }
            }

            shardsByTag[t] = [.. shards];
        }

        fallbackLanguage = IndexOfTag(tags, defaultLanguageTag);
        if (fallbackLanguage < 0)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The default language '{defaultLanguageTag}' is not one of the languages supplied."),
                nameof(defaultLanguageTag));
        }

        selected = fallbackLanguage;
        FormatCulture = CultureFor(tags[selected]);
        fallback = shippedCatalog;
    }

    /// <summary>Every language tag this catalog holds, once each, in the order they were supplied.</summary>
    public IReadOnlyList<string> Languages => tags;

    /// <summary>The language a miss in the selected one falls back to before the game's catalog is asked.</summary>
    public string DefaultLanguage => tags[fallbackLanguage];

    /// <summary>The language being resolved against right now.</summary>
    public string CurrentLanguage => tags[selected];

    /// <summary>
    /// The culture <see cref="Format"/> formats with: the SELECTED language's own, or the invariant culture
    /// when the tag names no culture this runtime knows. The string came out of that language's chunk, so
    /// its numbers and dates belong to that language rather than to whatever the thread happens to be set
    /// to.
    /// </summary>
    public CultureInfo FormatCulture { get; private set; } = CultureInfo.InvariantCulture;

    /// <summary>Selects a language by tag, ordinal ignoring case. False leaves the selection alone.</summary>
    /// <param name="tag">The BCP-47 tag.</param>
    public bool SelectLanguage(string? tag)
    {
        int found = tag is null ? -1 : IndexOfTag(tags, tag);
        if (found < 0)
        {
            return false;
        }

        selected = found;
        FormatCulture = CultureFor(tags[found]);
        return true;
    }

    /// <summary>
    /// Selects the language for a culture: its own tag, then its parent chain, so a client on <c>en-GB</c>
    /// resolves against a pack that ships <c>en</c>. False leaves the selection alone, which is the right
    /// answer for a culture the content has not been translated into: the default language is what a player
    /// sees until it is.
    /// </summary>
    /// <param name="culture">The culture to resolve against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="culture"/> is null.</exception>
    public bool SelectLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        for (CultureInfo? walk = culture; walk is not null && walk.Name.Length > 0; walk = Parent(walk))
        {
            if (SelectLanguage(walk.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The layered lookup: content, then the game's catalog, then the key itself as a visible non-fatal
    /// placeholder. It never throws.
    /// </summary>
    /// <param name="key">The derived key of contracts 12.1, <c>&lt;type&gt;.&lt;content key&gt;.&lt;field&gt;</c>.</param>
    public string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TryGet(key, out string value) ? value : key;
    }

    /// <summary>
    /// The non-throwing probe. <paramref name="value"/> is the key itself on a miss, which is what the whole
    /// stack does with an absent key.
    /// </summary>
    /// <param name="key">The derived key.</param>
    /// <param name="value">The resolved value, or the key when nothing carried it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public bool TryGet(string key, out string value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (TryGetFromContent(key, out value))
        {
            return true;
        }

        if (fallback is not null && fallback(key, out string shipped))
        {
            value = shipped;
            return true;
        }

        value = key;
        return false;
    }

    /// <summary>
    /// Content only, with the language fallback but WITHOUT the game's catalog under it. It is what a
    /// coverage sweep asks, because the question there is whether the PACK carries a string rather than
    /// whether anything does.
    /// </summary>
    /// <param name="key">The derived key.</param>
    /// <param name="value">The value the pack carries, or the key when it carries none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public bool TryGetFromContent(string key, out string value)
    {
        ArgumentNullException.ThrowIfNull(key);

        Span<byte> utf8 = stackalloc byte[ContentTextChunkCodec.MaxKeyBytes];
        if (!Encoding.UTF8.TryGetBytes(key, utf8, out int written))
        {
            // Past the derived-key bound of contracts 12.2, so no chunk can be carrying it.
            value = key;
            return false;
        }

        ReadOnlySpan<byte> probe = utf8[..written];
        if (TryResolve(selected, probe, out value)
            || (selected != fallbackLanguage && TryResolve(fallbackLanguage, probe, out value)))
        {
            return true;
        }

        value = key;
        return false;
    }

    /// <summary>
    /// The value as the UTF-8 bytes the chunk holds, with no string materialised at all. This is the
    /// resident form, and it is here because a caller measuring, hashing or comparing a content string has
    /// no business inflating it to UTF-16 first.
    /// </summary>
    /// <param name="key">The derived key, as UTF-8 bytes.</param>
    /// <param name="value">The value's bytes, or empty on a miss.</param>
    public bool TryGetUtf8(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        int[] shards = shardsByTag[selected];
        for (int i = 0; i < shards.Length; i++)
        {
            if (indexes[shards[i]].TryGetUtf8(key, out value))
            {
                return true;
            }
        }

        if (selected != fallbackLanguage)
        {
            int[] defaults = shardsByTag[fallbackLanguage];
            for (int i = 0; i < defaults.Length; i++)
            {
                if (indexes[defaults[i]].TryGetUtf8(key, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The resolved template with the arguments applied, through the SAFE formatting of contracts 12.3: a
    /// malformed translator-authored template falls back to the unformatted template rather than taking the
    /// frame loop down.
    /// </summary>
    /// <param name="key">The key whose value is the format template.</param>
    /// <param name="args">The format arguments.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public string Format(string key, params object?[] args) => SafeFormat(FormatCulture, Get(key), args);

    /// <summary>
    /// The never-throwing format every <see cref="Format"/> routes through, and the reason it can promise
    /// not to throw.
    /// <para>
    /// A malformed template is CONTENT arriving as data rather than a caller bug. A translation carrying
    /// <c>{1}</c> where the call site passes one argument throws the instant that text is drawn, and a Gui
    /// resolves inside the frame loop with nothing above it to catch, so one bad line in one language used
    /// to end the process. Falling back to the unformatted template leaves the text visibly wrong, which is
    /// what a content defect should look like, without taking the game down.
    /// </para>
    /// </summary>
    /// <param name="culture">The format provider.</param>
    /// <param name="template">The already-resolved template.</param>
    /// <param name="args">The arguments, or null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is null.</exception>
    public static string SafeFormat(IFormatProvider culture, string template, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(template);
        try
        {
            return string.Format(culture, template, args ?? []);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// One language's answer, through the bounded cache. A miss is one UTF-8 decode over a slice the
    /// catalog is already holding, and the cache is what stops a frame loop paying for that decode on every
    /// frame.
    /// </summary>
    bool TryResolve(int language, ReadOnlySpan<byte> key, out string value)
    {
        int[] shards = shardsByTag[language];
        for (int i = 0; i < shards.Length; i++)
        {
            int shard = shards[i];
            if (!indexes[shard].TryGetOffset(key, out int entryOffset))
            {
                continue;
            }

            long tag = ((long)shard << 32) | (uint)(entryOffset + 1);
            int slot = (int)((ulong)tag * 0x9E3779B97F4A7C15UL >> 55) & (CacheEntries - 1);
            if (cacheTags[slot] == tag && cacheValues[slot] is string cached)
            {
                value = cached;
                return true;
            }

            value = Encoding.UTF8.GetString(indexes[shard].ValueAt(entryOffset));
            cacheTags[slot] = tag;
            cacheValues[slot] = value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    static CultureInfo CultureFor(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            // A pack may ship a language this runtime has no culture for, which is a formatting question
            // rather than a lookup one: the strings still resolve, the numbers inside them are invariant.
            return CultureInfo.InvariantCulture;
        }
    }

    static CultureInfo? Parent(CultureInfo culture)
    {
        CultureInfo parent = culture.Parent;
        return ReferenceEquals(parent, culture) ? null : parent;
    }

    static int IndexOfTag(IReadOnlyList<string> haystack, string tag)
    {
        for (int i = 0; i < haystack.Count; i++)
        {
            if (string.Equals(haystack[i], tag, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
