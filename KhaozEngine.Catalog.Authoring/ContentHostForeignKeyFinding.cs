namespace KhaozEngine.Catalog.Authoring;

/// <summary>A host foreign key whose delete action would write outside the catalog during reset.</summary>
/// <param name="HostTable">The host table declaring the key.</param>
/// <param name="ReferencedTable">The catalog table the key references.</param>
/// <param name="DeleteAction">The provider's declared delete action, such as CASCADE or SET NULL.</param>
public sealed record ContentHostForeignKeyFinding(string HostTable, string ReferencedTable, string DeleteAction);
