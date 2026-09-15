using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// Pass 1 of the sweep, STRUCTURE (spec 5.3): ids, keys, families, blocks, chunk sizes and type identity.
/// It walks every id of every type once, which is why the id checks and the family block checks share it.
/// <para>
/// Two of its checks are PUBLISH ONLY and compare the candidate against <c>previous</c>. They are skipped
/// when it is null and <c>KEC0000</c> names them, which is a property of the argument rather than a mode
/// flag.
/// </para>
/// </summary>
internal static class ContentKeyChecks
{
    /// <summary>The key length cap of contracts 5.3, which matches the consumers' own key columns.</summary>
    internal const int MaxKeyLength = 64;

    internal static void Run(ContentValidationRun run)
    {
        CheckTypeIdentity(run);

        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            CheckRows(run, registration);
        }

        CheckFamilies(run);
    }

    /// <summary>
    /// <c>KEC0028</c>, the chunk slot rule of contracts 4.5, and <c>KEC0029</c>, a type whose identity moved
    /// after its first publish.
    /// <para>
    /// <c>KEC0028</c> is defence in depth here: <see cref="ContentTypeRegistry.RegisterContentType"/> refuses
    /// an illegal slot count at registration, so a live registry cannot carry one. The check stays because
    /// the registry is not the only future source of a type declaration, and because a validator that
    /// trusted its inputs would be the wrong shape.
    /// </para>
    /// <para>
    /// <c>KEC0029</c> sees the half of the rule a pair of SNAPSHOTS can answer: a type that carried rows in
    /// the previous version and is no longer registered has had its id reassigned or dropped, which
    /// repaginates every chunk hash derived from it. The type-key and chunk-slot halves need the durable
    /// type declaration of the authoring store's <c>catalog_type</c> table, which no snapshot carries, so
    /// they are the store's to enforce.
    /// </para>
    /// </summary>
    static void CheckTypeIdentity(ContentValidationRun run)
    {
        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            int slots = registration.ChunkSlots;
            if (slots < ContentTypeRegistry.MinChunkSlots
                || slots > ContentTypeRegistry.MaxChunkSlots
                || (slots & (slots - 1)) != 0)
            {
                run.Add(
                    registration.Type,
                    0,
                    "KEC0028",
                    FormattableString.Invariant(
                        $"Type '{registration.TypeKey}' declares {slots} chunk slots, which must be a power of two between {ContentTypeRegistry.MinChunkSlots} and {ContentTypeRegistry.MaxChunkSlots}."));
            }
        }

        ContentSnapshot? previous = run.Previous;
        if (previous is null)
        {
            return;
        }

        foreach (ContentTypeId type in previous.Types)
        {
            if (run.Registry.TryGet(type, out _) || previous.Rows(type).Count == 0)
            {
                continue;
            }

            run.Add(
                type,
                0,
                "KEC0029",
                FormattableString.Invariant(
                    $"Type id {type.Value} carried {previous.Rows(type).Count} published rows at version {previous.VersionNumber} and is not registered now. A type id is fixed at its first publish, because a reassignment repaginates every chunk hash and every page stamp derived from it."));
        }
    }

    /// <summary>
    /// The per-row structure checks: <c>KEC0009</c> and <c>KEC0042</c> on the id, <c>KEC0001</c> and
    /// <c>KEC0002</c> on the key, <c>KEC0036</c> on two live rows sharing an id, and the publish-only
    /// <c>KEC0003</c> on a key that moved.
    /// </summary>
    static void CheckRows(ContentValidationRun run, ContentTypeRegistration registration)
    {
        IReadOnlyList<ContentRow> rows = run.Candidate.Rows(registration.Type);
        if (rows.Count == 0)
        {
            return;
        }

        var keys = new Dictionary<ContentKey, int>(rows.Count);
        var liveIds = new HashSet<int>(rows.Count);

        foreach (ContentRow row in rows)
        {
            if (row.Id <= 0)
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0009",
                    FormattableString.Invariant(
                        $"Row '{row.Key}' of type '{registration.TypeKey}' carries definition id {row.Id}. Id 0 is reserved and means no content, and a negative id is never valid."));
            }

            if (registration.MaxDefinitionId is int ceiling && row.Id > ceiling)
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0042",
                    FormattableString.Invariant(
                        $"Row '{row.Key}' carries definition id {row.Id}, over the ceiling of {ceiling} type '{registration.TypeKey}' declared at registration. A type declares one when a format it is carried in cannot hold a bigger number."));
            }

            string? malformed = KeyDefect(row.Key.Utf8);
            if (malformed is not null)
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0001",
                    FormattableString.Invariant(
                        $"Row {row.Id} of type '{registration.TypeKey}' carries key '{row.Key}', which is {malformed}. A key is 1 to {MaxKeyLength} characters of a-z, 0-9 and underscore, with no leading digit, no leading or trailing underscore and no double underscore."));
            }

            if (keys.TryGetValue(row.Key, out int firstId))
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0002",
                    FormattableString.Invariant(
                        $"Key '{row.Key}' is on rows {firstId} and {row.Id} of type '{registration.TypeKey}'. A key is unique within its type, because it is what an author, a config file, a console and a localization key all name the row by."));
            }
            else
            {
                keys.Add(row.Key, row.Id);
            }

            if (!row.IsRetired && !liveIds.Add(row.Id))
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0036",
                    FormattableString.Invariant(
                        $"Two live rows of type '{registration.TypeKey}' carry definition id {row.Id}. An id is unique within its type and is never reused, which is the property the allocator's reserve-before-issue discipline exists to keep."));
            }

            CheckKeyDidNotMove(run, registration, row);
        }
    }

    /// <summary>
    /// <c>KEC0003</c>, PUBLISH ONLY. A key is immutable once published, so a rename is a retire plus a new
    /// row plus a remap rule rather than an edit. Skipped whole when <c>previous</c> is null.
    /// </summary>
    static void CheckKeyDidNotMove(ContentValidationRun run, ContentTypeRegistration registration, ContentRow row)
    {
        if (run.Previous is not ContentSnapshot previous
            || !previous.TryGetRow(registration.Type, row.Id, out ContentRow? published)
            || published.Key == row.Key)
        {
            return;
        }

        run.Add(
            registration.Type,
            row.Id,
            "KEC0003",
            FormattableString.Invariant(
                $"Row {row.Id} of type '{registration.TypeKey}' was published as '{published.Key}' and the candidate carries '{row.Key}'. A published key is immutable, and a rename is a retire plus a new row plus a remap rule."));
    }

    /// <summary>
    /// <c>KEC0010</c>, <c>KEC0011</c>, <c>KEC0012</c> and <c>KEC0037</c>, the family block checks of
    /// contracts 5.2.
    /// <para>
    /// <b>They have no input in phase 1 and so cannot fire.</b> A family is an AUTHORING declaration
    /// (<c>catalog_family</c> and <c>catalog_family_block</c>), the row's claim on one is authoring data
    /// too, and neither travels in a pack: a boot decodes rows and rules and nothing about families. The
    /// four-argument signature of spec 5.1 carries no family list, and <see cref="ContentSnapshot"/> carries
    /// neither the declarations nor a per-row claim, so this pass has nothing to compare. The checks belong
    /// to whoever holds both halves, which is the publish once the authoring store ships them.
    /// </para>
    /// <para>
    /// Left as the named hook rather than as four codes nobody can find, because the codes are issued and a
    /// reader looking for <c>KEC0037</c> has to land somewhere that says why it is quiet.
    /// </para>
    /// </summary>
    static void CheckFamilies(ContentValidationRun run)
    {
        _ = run;
    }

    /// <summary>
    /// The key rules of contracts 5.3, as a phrase naming the first defect, or null when the key is well
    /// formed. The rules are the VALIDATOR's rather than <see cref="ContentKey"/>'s, so a bad key reaches
    /// the sweep intact and a bulk import reports every one of them in a single pass.
    /// </summary>
    static string? KeyDefect(ReadOnlySpan<byte> key)
    {
        if (key.Length == 0)
        {
            return "empty";
        }

        if (key.Length > MaxKeyLength)
        {
            return FormattableString.Invariant($"{key.Length} characters long");
        }

        for (int i = 0; i < key.Length; i++)
        {
            byte c = key[i];
            bool allowed = c is >= (byte)'a' and <= (byte)'z' || c is >= (byte)'0' and <= (byte)'9' || c == (byte)'_';
            if (!allowed)
            {
                return FormattableString.Invariant($"outside the character set at position {i}");
            }

            if (c == (byte)'_' && i > 0 && key[i - 1] == (byte)'_')
            {
                return FormattableString.Invariant($"a double underscore at position {i}");
            }
        }

        if (key[0] is >= (byte)'0' and <= (byte)'9')
        {
            return "a leading digit";
        }

        if (key[0] == (byte)'_')
        {
            return "a leading underscore";
        }

        if (key[^1] == (byte)'_')
        {
            return "a trailing underscore";
        }

        return null;
    }
}
