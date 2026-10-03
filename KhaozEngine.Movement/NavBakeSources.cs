using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace KhaozEngine.Movement;

/// <summary>Mutable builder of labelled SHA-256 digests naming every input a bake reads. Labels are 1 to 64
/// characters from <c>a</c> to <c>z</c>, <c>0</c> to <c>9</c>, <c>.</c>, <c>_</c>, <c>-</c> and <c>/</c>, and are
/// unique. Creating or loading a bake snapshots the builder, so later edits never change a stored identity.</summary>
public sealed class NavBakeSources
{
    private const int DigestLength = 32;
    private readonly List<(string Label, byte[] Digest)> _entries = [];

    /// <summary>Adds an exact 32-byte SHA-256 digest under <paramref name="label"/>.</summary>
    /// <exception cref="ArgumentException">The label is invalid or already present, or the digest is not 32 bytes.</exception>
    public NavBakeSources Add(string label, ReadOnlySpan<byte> sha256)
    {
        CheckLabel(label);
        if (sha256.Length != DigestLength)
            throw new ArgumentException("A source digest must be exactly 32 bytes of SHA-256.", nameof(sha256));
        _entries.Add((label, sha256.ToArray()));
        return this;
    }

    /// <summary>Adds the SHA-256 digest of <paramref name="content"/> under <paramref name="label"/>.</summary>
    /// <exception cref="ArgumentException">The label is invalid or already present.</exception>
    public NavBakeSources AddHashOf(string label, ReadOnlySpan<byte> content)
    {
        CheckLabel(label);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(content);
        _entries.Add((label, hash.GetHashAndReset()));
        return this;
    }

    /// <summary>Adds the SHA-256 digest of the rest of <paramref name="content"/> under <paramref name="label"/>.
    /// The label is checked before the stream is read. Stream errors propagate.</summary>
    /// <exception cref="ArgumentException">The label is invalid or already present.</exception>
    public NavBakeSources AddHashOf(string label, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        CheckLabel(label);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
        _entries.Add((label, hash.GetHashAndReset()));
        return this;
    }

    /// <summary>The labels in ordinal order.</summary>
    public IReadOnlyList<string> Labels
    {
        get
        {
            var labels = new string[_entries.Count];
            for (int i = 0; i < labels.Length; i++) labels[i] = _entries[i].Label;
            Array.Sort(labels, StringComparer.Ordinal);
            return labels;
        }
    }

    /// <summary>An ordinal-sorted copy of the entries with copied digests.</summary>
    internal IReadOnlyList<(string Label, byte[] Digest)> Snapshot()
    {
        var copy = new (string Label, byte[] Digest)[_entries.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = (_entries[i].Label, (byte[])_entries[i].Digest.Clone());
        Array.Sort(copy, static (a, b) => string.CompareOrdinal(a.Label, b.Label));
        return copy;
    }

    private void CheckLabel(string label)
    {
        NavBakeIdentity.CheckName(label, nameof(label), "Source label");
        foreach ((string existing, _) in _entries)
            if (string.Equals(existing, label, StringComparison.Ordinal))
                throw new ArgumentException($"Source label '{label}' is already present.", nameof(label));
    }
}
