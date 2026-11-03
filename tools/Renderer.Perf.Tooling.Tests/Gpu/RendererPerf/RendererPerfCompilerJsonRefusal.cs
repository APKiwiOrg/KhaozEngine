namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Stable reasons the compiler inventory and compiler file reader refuses a document.</summary>
internal enum RendererPerfCompilerJsonRefusal
{
    /// <summary>The document names another schema, is missing a required value or has inconsistent completeness.</summary>
    Schema,
    /// <summary>The source table has an invalid or duplicated hash, a stage names a foreign source, or a source
    /// is unused or out of first-reference order.</summary>
    SourceTable,
    /// <summary>The decoded inventory breaks a canonical construction rule, such as an uncanonicalized duplicate
    /// descriptor or a recipe hash that disagrees with its retained values.</summary>
    Inventory,
    /// <summary>A table count or the encoded bytes exceed their capacity.</summary>
    Capacity
}
