namespace KhaozEngine.Catalog;

/// <summary>The rule that refused a required field lookup in a loaded content runtime.</summary>
public enum ContentFieldLookupRefusal
{
    /// <summary>The runtime's registry does not carry the requested type.</summary>
    TypeUnregistered,
    /// <summary>The registered type's schema does not declare the requested field.</summary>
    FieldUndeclared,
}
