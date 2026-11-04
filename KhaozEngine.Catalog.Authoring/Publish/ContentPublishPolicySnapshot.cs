using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// A policy's read-only row view, detached from the actual baseline and candidate. It exposes neither
/// their backing arrays nor the store or registry used to build them.
/// </summary>
public sealed class ContentPublishPolicySnapshot
{
    readonly Dictionary<ContentTypeId, IReadOnlyList<ContentPublishPolicyRow>> _rows = [];
    readonly Dictionary<(ContentTypeId Type, string Key), ContentPublishPolicyRow> _byKey = [];

    internal ContentPublishPolicySnapshot(ContentSnapshot? snapshot, ContentTypeRegistry registry)
    {
        VersionNumber = snapshot?.VersionNumber ?? 0;
        if (snapshot is null)
        {
            return;
        }

        foreach (ContentTypeRegistration type in registry.ByTypeId)
        {
            IReadOnlyList<ContentRow> source = snapshot.Rows(type.Type);
            var rows = new ContentPublishPolicyRow[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                rows[i] = new ContentPublishPolicyRow(source[i], type.Schema);
                _byKey.TryAdd((type.Type, rows[i].Key), rows[i]);
            }

            _rows.Add(type.Type, Array.AsReadOnly(rows));
        }
    }

    /// <summary>The version represented by this view, zero for an empty initial baseline.</summary>
    public int VersionNumber { get; }

    /// <summary>Rows in id order, including retired rows. The returned collection cannot be changed.</summary>
    public IReadOnlyList<ContentPublishPolicyRow> Rows(ContentTypeId type)
        => _rows.TryGetValue(type, out IReadOnlyList<ContentPublishPolicyRow>? rows) ? rows : Array.Empty<ContentPublishPolicyRow>();

    /// <summary>Finds a row by its stable type and ordinal key, without exposing any mutable owner.</summary>
    public bool TryGetRow(ContentTypeId type, string key, [NotNullWhen(true)] out ContentPublishPolicyRow? row)
    {
        return _byKey.TryGetValue((type, key), out row);
    }
}
