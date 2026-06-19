using System;

namespace KhaozEngine.Ecs;

/// <summary>
/// Optional stable persistence key for a component type. When present, <see cref="WorldSerializer"/> writes
/// and reads the component under this id instead of <see cref="Type.FullName"/>, so renaming or moving the
/// struct does not break existing saves. Ids must be unique within a world's component set.
/// Note: if you add <c>[ComponentId]</c> to a type that previously serialized under its <c>Type.FullName</c>,
/// existing saves stored under the old name will fail to load; register a document migration via
/// <see cref="WorldSerializer.RegisterMigration"/> to rename the key in older save documents.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class ComponentIdAttribute : Attribute
{
    public string Id { get; }
    public ComponentIdAttribute(string id) => Id = id;
}
