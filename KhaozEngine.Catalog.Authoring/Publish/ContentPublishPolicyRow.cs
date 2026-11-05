using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// A detached policy row. Named fields retain their catalog kinds and absence bits. Byte payloads are
/// copied on capture and on access, so a policy cannot change either the publication or its own view.
/// </summary>
public sealed class ContentPublishPolicyRow
{
    readonly Dictionary<string, ContentFieldValue> _fields = new(StringComparer.Ordinal);

    internal ContentPublishPolicyRow(ContentRow row, ContentFieldSchema schema)
    {
        Type = row.Type;
        Id = row.Id;
        Key = row.Key.ToString();
        IsRetired = row.IsRetired;
        ParentId = row.ParentId;
        for (int i = 0; i < row.Fields.Count && i < schema.Fields.Count; i++)
        {
            _fields.Add(schema.Fields[i].Name, Copy(row.Fields[i]));
        }
    }

    /// <summary>The content type's stable numeric identity.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The row id, provisional for a newly added row at preview.</summary>
    public int Id { get; }

    /// <summary>The row's stable ordinal key.</summary>
    public string Key { get; }

    /// <summary>Whether this row is retired.</summary>
    public bool IsRetired { get; }

    /// <summary>The row's inheritance parent id.</summary>
    public int ParentId { get; }

    /// <summary>Reads one typed field by its ordinal schema name, copying any byte payload.</summary>
    /// <exception cref="KeyNotFoundException">The row carries no field under that name.</exception>
    public ContentFieldValue GetValue(string name) => Copy(_fields[name]);

    /// <summary>Creates a finding with this row's structured type, id and key.</summary>
    public ContentFinding Finding(string code, string reason) => new(Type, Id, code, reason) { Key = Key };

    static ContentFieldValue Copy(ContentFieldValue value)
        => value.IsAbsent || !ContentFieldValue.StoresBytes(value.Kind)
            ? value : ContentFieldValue.OfBytes(value.Kind, value.Bytes.ToArray());
}
