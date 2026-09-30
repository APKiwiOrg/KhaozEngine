using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

public sealed partial class SqlServerContentAuthoringStore
{
    // Stored settings describe the catalog before this build synchronizes its declarations.
    async Task<IReadOnlyList<ContentBundleType>> ReadBundleTypesAsync(
        SqlServerCatalogScope scope, CancellationToken cancellationToken)
    {
        var types = new List<ContentBundleType>();
        if (_useStoredTypeMetadata)
        {
            await using SqlCommand command = Command(scope,
                "SELECT type_id, type_key, chunk_slots, default_visibility, max_definition_id FROM dbo.catalog_type ORDER BY type_id;");
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var type = new ContentTypeId((ushort)reader.GetInt32(0));
                types.Add(new ContentBundleType(type, reader.GetString(1),
                    (ContentVisibility)reader.GetInt32(3), reader.GetInt32(2),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), RequireType(type).Schema));
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
