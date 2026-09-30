using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

public sealed partial class SqliteContentAuthoringStore
{
    // Stored settings describe the catalog before this build synchronizes its declarations.
    async Task<IReadOnlyList<ContentBundleType>> ReadBundleTypesAsync(CancellationToken cancellationToken)
    {
        var types = new List<ContentBundleType>();
        if (_useStoredTypeMetadata)
        {
            using SqliteCommand command = Command(
                "SELECT type_id, type_key, chunk_slots, default_visibility, max_definition_id FROM catalog_type ORDER BY type_id;");
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var type = new ContentTypeId((ushort)reader.GetInt64(0));
                types.Add(new ContentBundleType(type, reader.GetString(1),
                    (ContentVisibility)reader.GetInt64(3), (int)reader.GetInt64(2),
                    reader.IsDBNull(4) ? null : (int)reader.GetInt64(4), RequireType(type).Schema));
            }

            return types;
        }

        IReadOnlyList<ContentTypeRegistration> registrations = _registry.ByTypeId;
        for (int i = 0; i < registrations.Count; i++)
        {
            ContentTypeRegistration registration = registrations[i];
            types.Add(new ContentBundleType(
                registration.Type,
                registration.TypeKey,
                registration.DefaultVisibility,
                registration.ChunkSlots,
                registration.MaxDefinitionId,
                registration.Schema));
        }

        return types;
    }
}
