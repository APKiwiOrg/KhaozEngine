using System;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// Every digest in the pack, contracts 7.3 and 15 and spec section 7.8. SHA-256, lower hex, domain
/// separated under <c>kec/</c> with its own sub-domain per digest and the scheme version folded into the
/// prefix, so a head gating on one digest can never accidentally agree with a head gating on another.
/// </summary>
public class ContentHashTests
{
    static readonly byte[] Canonical = "the same bytes under five domains"u8.ToArray();

    [Fact]
    public void EachSubDomainProducesADifferentDigestOverIdenticalBytes()
    {
        string text = Encoding.UTF8.GetString(Canonical);
        string[] digests =
        [
            ContentHash.OfChunk(Canonical),
            ContentHash.OfRuleChunk(Canonical),
            ContentHash.OfTextChunk(Canonical),
            ContentHash.OfServerManifest(text),
            ContentHash.OfClientManifest(text),
        ];

        Assert.Equal(digests.Length, digests.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NoTwoSubDomainStringsAreEqual()
    {
        string[] subDomains =
        [
            ContentHash.ChunkDomain,
            ContentHash.RuleDomain,
            ContentHash.TextDomain,
            ContentHash.ServerManifestDomain,
            ContentHash.ClientManifestDomain,
        ];

        Assert.Equal(subDomains.Length, subDomains.Distinct(StringComparer.Ordinal).Count());
        Assert.All(subDomains, d => Assert.StartsWith("kec/", d, StringComparison.Ordinal));
        Assert.All(subDomains, d => Assert.EndsWith("/", d, StringComparison.Ordinal));
    }

    [Fact]
    public void TheSchemeVersionIsFoldedIntoEveryDigest()
    {
        // The prefix is rebuilt here by hand rather than read off the type, so a change to the construction
        // fails this test instead of silently agreeing with itself.
        Assert.Equal(Local(ContentHash.ChunkDomain, ContentHash.SchemeVersion), ContentHash.OfChunk(Canonical));
        Assert.Equal(Local(ContentHash.RuleDomain, ContentHash.SchemeVersion), ContentHash.OfRuleChunk(Canonical));
        Assert.Equal(Local(ContentHash.TextDomain, ContentHash.SchemeVersion), ContentHash.OfTextChunk(Canonical));

        // A bumped scheme version re-digests everything, which is precisely why it exists.
        Assert.NotEqual(Local(ContentHash.ChunkDomain, ContentHash.SchemeVersion + 1), ContentHash.OfChunk(Canonical));
        Assert.NotEqual(Local(ContentHash.RuleDomain, ContentHash.SchemeVersion + 1), ContentHash.OfRuleChunk(Canonical));

        string text = Encoding.UTF8.GetString(Canonical);
        Assert.Equal(LocalText(ContentHash.ServerManifestDomain, ContentHash.SchemeVersion, text), ContentHash.OfServerManifest(text));
        Assert.Equal(LocalText(ContentHash.ClientManifestDomain, ContentHash.SchemeVersion, text), ContentHash.OfClientManifest(text));
        Assert.NotEqual(LocalText(ContentHash.ServerManifestDomain, ContentHash.SchemeVersion + 1, text), ContentHash.OfServerManifest(text));

        static string Local(string subDomain, int schemeVersion)
        {
            byte[] prefix = Encoding.UTF8.GetBytes(subDomain + schemeVersion.ToString(CultureInfo.InvariantCulture) + "\n");
            byte[] whole = [.. prefix, .. Canonical];
            return Convert.ToHexStringLower(SHA256.HashData(whole));
        }

        static string LocalText(string subDomain, int schemeVersion, string canonicalText)
        {
            string whole = subDomain + schemeVersion.ToString(CultureInfo.InvariantCulture) + "\n" + canonicalText;
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(whole)));
        }
    }

    [Fact]
    public void EveryDigestIsSixtyFourLowerCaseHexCharacters()
    {
        string[] digests =
        [
            ContentHash.OfChunk(Canonical),
            ContentHash.OfRuleChunk(Canonical),
            ContentHash.OfTextChunk(Canonical),
            ContentHash.OfServerManifest("manifest"),
            ContentHash.OfClientManifest("manifest"),
            ContentHash.OfBytesForKind([.. "KECC"u8, .. Canonical])!,
        ];

        foreach (string digest in digests)
        {
            Assert.Equal(64, digest.Length);
            Assert.All(digest, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'), $"'{c}' is not lower hex"));
        }
    }

    [Fact]
    public void OfBytesForKindDispatchesOnTheMagic()
    {
        Assert.Equal(ContentHash.OfChunk([.. "KECC"u8, .. Canonical]), ContentHash.OfBytesForKind([.. "KECC"u8, .. Canonical]));
        Assert.Equal(ContentHash.OfRuleChunk([.. "KECR"u8, .. Canonical]), ContentHash.OfBytesForKind([.. "KECR"u8, .. Canonical]));
        Assert.Equal(ContentHash.OfTextChunk([.. "KECT"u8, .. Canonical]), ContentHash.OfBytesForKind([.. "KECT"u8, .. Canonical]));

        // A manifest is hashed over its canonical TEXT after decode, never over its file bytes, so there is
        // no right answer to give here. An unknown magic and a run of bytes too short to carry one are the
        // same refusal, taken before any length is read.
        Assert.Null(ContentHash.OfBytesForKind([.. "KECM"u8, .. Canonical]));
        Assert.Null(ContentHash.OfBytesForKind([.. "KECZ"u8, .. Canonical]));
        Assert.Null(ContentHash.OfBytesForKind("KEC"u8));
        Assert.Null(ContentHash.OfBytesForKind(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ManifestTextLengthPrefixesStringsAndMarksNull()
    {
        var builder = new StringBuilder();
        ContentHash.AppendText(builder, "stone");
        ContentHash.AppendText(builder, null);
        ContentHash.AppendText(builder, string.Empty);
        Assert.Equal("5:stone - 0: ", builder.ToString());

        // A delimiter inside an authored key cannot make two different manifests digest the same, which is
        // the whole reason the length goes in front.
        var a = new StringBuilder();
        var b = new StringBuilder();
        ContentHash.AppendText(a, "iron");
        ContentHash.AppendText(a, "sword");
        ContentHash.AppendText(b, "iron sword");
        Assert.NotEqual(a.ToString(), b.ToString());
    }

    [Fact]
    public void ManifestTextFormatsNumbersThroughTheInvariantCulture()
    {
        // A negative number is what picks up a culture's own minus sign. The hostile culture runs on its own
        // thread so it cannot leak into another test through a pooled one.
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "−";

        string here = Numbers();
        string? there = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = hostile;
                there = Numbers();
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.Start();
        thread.Join();
        failure?.Throw();

        Assert.Equal("-2048 1 ", here);
        Assert.Equal(here, there);

        static string Numbers()
        {
            var builder = new StringBuilder();
            ContentHash.AppendNumber(builder, -2048);
            builder.Append(' ');
            ContentHash.AppendNumber(builder, ContentHash.SchemeVersion);
            builder.Append(' ');
            return builder.ToString();
        }
    }

    [Fact]
    public void TheSchemeVersionIsTheFormatsOwn()
    {
        Assert.Equal(ContentPackFormat.HashSchemeVersion, ContentHash.SchemeVersion);
    }

    [Fact]
    public void TheStreamingChunkDigestMatchesTheWholeBufferOne()
    {
        byte[] header = Canonical[..8];
        byte[] body = Canonical[8..];
        Assert.Equal(
            ContentHash.OfChunk(Canonical),
            ContentHash.OfChunkStreaming(header, body, ContentHash.ChunkDomain));
    }
}
