using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Spec 12.2's thirteen checks over a stored container, accumulating and never throwing, following
/// contracts 10.4's shape and <c>JsonSchemaValidator</c>'s run-to-the-end sweep
/// (<c>KhaozEngine.Content/JsonSchemaValidator.cs:11-101</c>).
/// <para>
/// <b>It is PURE.</b> No store read, no ambient state, no logging, no counter and no throw for a content
/// reason. A throw from it is a bug in the validator. The caller logs, counts and quarantines, and
/// <see cref="InstanceValidationTelemetry"/> is the one named place the first two happen.
/// </para>
/// <para>
/// <b>Two doors over ONE sweep.</b> <see cref="Validate"/> takes a whole decoded container, which is what
/// <see cref="ItemContainerPageCodec.TryDecode"/> hands back, and is a loop over
/// <see cref="ValidateEntry"/> plus the two answers that are CONTAINER wide: check 10's instance id
/// uniqueness, and the report. When <c>ItemContainerPage</c> arrives it wraps the same header plus entries
/// and calls the same method, so nothing here changes shape when paging lands.
/// </para>
/// <para>
/// <b>Checks 6 and 7 are DERIVED from the property registry</b> rather than from a list here, walking the
/// same <see cref="InstanceReferenceTarget"/> descriptors <see cref="InstanceRemapPass"/> walks, in the same
/// recursive order, over the same nested payloads. A kind cannot be remapped-but-not-validated or
/// validated-but-not-remapped, and a game kind at or above 1,024 gets drift detection by declaring its
/// shape and nothing else. Check 8 stays hand written, because a tier ordinal is not a content id: it is a
/// key INTO the row check 7 already resolved.
/// </para>
/// <para>
/// <b>An entry whose quarantined flag is set carries a WRAPPER rather than a payload.</b> The whole-container
/// door reports it from the stored reason and stamp without sweeping the wrapper as payload bytes. The
/// standalone door still takes the ORIGINAL bytes after a caller unwraps them, which is how the first load
/// after a missing remap rule lands restores the item exactly.
/// </para>
/// </summary>
public static partial class InstanceValidator
{
    /// <summary>
    /// Sweeps a decoded container and reports everything it found. Never throws for a byte.
    /// </summary>
    /// <param name="page">The page buffer the entries were decoded from, which is where their payload
    /// windows point.</param>
    /// <param name="header">The decoded header, whose <see cref="PageHeader.ContentVersion"/> is the stamp
    /// every live entry finding carries. A stored quarantine carries its wrapper's stamp.</param>
    /// <param name="entries">The decoded entries, as many as the decode wrote.</param>
    /// <param name="properties">The property kinds this build knows. An unregistered kind is kept verbatim
    /// and never inspected, which is contracts 9.4.</param>
    /// <param name="types">The content types the active pack registers, which is what turns a reference
    /// target's type KEY into the type id the snapshot is read by.</param>
    /// <param name="snapshot">The ACTIVE content version. Nothing is read from a store, a file or an
    /// ambient static inside this call.</param>
    /// <exception cref="ArgumentNullException">A registry or the snapshot is null.</exception>
    /// <exception cref="ArgumentException">An entry's payload window lies outside
    /// <paramref name="page"/>, which means the entries did not come from this page. That is a caller
    /// error rather than a fact about the bytes, which is why it throws while nothing about the bytes
    /// ever does.</exception>
    public static InstanceValidationReport Validate(
        ReadOnlySpan<byte> page,
        in PageHeader header,
        ReadOnlySpan<PageEntry> entries,
        InstancePropertyRegistry properties,
        ContentTypeRegistry types,
        IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(snapshot);

        var findings = new List<InstanceValidationFinding>();
        var content = new ContentReferences(types, snapshot);
        for (int index = 0; index < entries.Length; index++)
        {
            PageEntry entry = entries[index];
            ReadOnlySpan<byte> payload = Window(page, entry);
            if (entry.Quarantined)
            {
                ReportStoredQuarantine(payload, entry, header.ContentVersion, findings);
                continue;
            }

            _ = Sweep(payload, entry, header.ContentVersion, properties, content, findings, out _);
        }

        DuplicateInstanceIds(entries, header.ContentVersion, findings);
        return new InstanceValidationReport(
            header.PageIndex, header.ContentVersion, snapshot.VersionNumber, entries.Length, findings);
    }

    /// <summary>
    /// Sweeps ONE entry, which is the standalone door a caller holding one item without a container uses.
    /// It runs every check except 10, which is container wide by nature and cannot be answered here.
    /// </summary>
    /// <param name="payload">The entry's payload bytes. <see cref="PageEntry.PayloadStart"/> and
    /// <see cref="PageEntry.PayloadLength"/> index a page this door is not handed, so the bytes arrive
    /// here instead.</param>
    /// <param name="entry">The entry's own facts: its slot, definition, count and instance id.</param>
    /// <param name="stampedVersion">The page stamp the record was read under, which every finding carries
    /// and which a <see cref="QuarantineWrapper"/> stores.</param>
    /// <param name="properties">The property kinds this build knows.</param>
    /// <param name="types">The content types the active pack registers.</param>
    /// <param name="snapshot">The active content version.</param>
    /// <param name="finding">The finding that DECIDES the entry: the quarantine if one fired, else the
    /// retire, else the tolerated policy finding, else the empty finding. An entry may carry both policy
    /// findings at once and only the whole-container <see cref="Validate"/> reports both.</param>
    /// <exception cref="ArgumentNullException">A registry or the snapshot is null.</exception>
    public static InstanceValidationOutcome ValidateEntry(
        ReadOnlySpan<byte> payload,
        in PageEntry entry,
        int stampedVersion,
        InstancePropertyRegistry properties,
        ContentTypeRegistry types,
        IContentSnapshot snapshot,
        out InstanceValidationFinding finding)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(snapshot);

        return Sweep(
            payload,
            entry,
            stampedVersion,
            properties,
            new ContentReferences(types, snapshot),
            sink: null,
            out finding);
    }

    /// <summary>
    /// The ONE sweep both doors run, in spec 12.2's own order. It stops at the first QUARANTINE, because
    /// every later check would be reading bytes already shown to mean nothing, and it runs past a policy
    /// finding, because those change nothing about how the bytes are read.
    /// </summary>
    static InstanceValidationOutcome Sweep(
        ReadOnlySpan<byte> payload,
        in PageEntry entry,
        int stampedVersion,
        InstancePropertyRegistry properties,
        ContentReferences content,
        List<InstanceValidationFinding>? sink,
        out InstanceValidationFinding deciding)
    {
        deciding = default;

        // Checks 1 to 5 are ONE call, because the payload decoder already is them: the three canonical
        // rules, the cap, every declared length, each known kind's shape and codec, and the one level
        // nesting limit. A second copy of them here is the thing that drifts.
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Span<PayloadField> nested = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(properties, payload, fields, out int fieldCount, out string? token))
        {
            return Quarantine(entry, stampedVersion, CheckForToken(token), token!, sink, ref deciding);
        }

        ReadOnlySpan<PayloadField> decoded = fields[..fieldCount];

        // Check 6, the entry's own definition. A retired row still RESOLVES (contracts 5.1 keeps it in the
        // pack forever), which is exactly why check 13 exists below.
        if (!content.ResolvesItem(entry.DefinitionId))
        {
            return Quarantine(
                entry, stampedVersion, 6, InstanceQuarantineReason.UnknownDefinition, sink, ref deciding);
        }

        // Checks 6, 7 and 8 through the registered reference targets, at every depth, and check 13's
        // socket clause on the way past.
        bool retired = content.IsItemRetired(entry.DefinitionId);
        Span<ulong> values = stackalloc ulong[SlotBuffer * 2];
        var walk = new WalkContext(properties, content, values[..SlotBuffer], nested, values[SlotBuffer..]);
        if (!WalkFields(payload, decoded, walk, nestedLevel: false, ref retired, out int check, out string? reason))
        {
            return Quarantine(entry, stampedVersion, check, reason!, sink, ref deciding);
        }

        // Check 9. The rule is task 6's rather than a second copy of it, with the definition-declared half
        // left to the caller: this door is handed an entry and its bytes, never a definition's declaration.
        if (InstanceIdAllocator.NeedsInstanceId(payload.Length, DeclaredInstanceProperties.None)
            && entry.InstanceId == 0)
        {
            return Quarantine(
                entry, stampedVersion, 9, InstanceQuarantineReason.InstanceIdMissing, sink, ref deciding);
        }

        // Check 11. Durability and sockets are both per INSTANCE, so an entry carrying either cannot be a
        // stack of several: contracts 10.4 refuses the pairing at publish and this is the same rule read
        // off the bytes.
        if (entry.Count > 1 && CarriesPerInstanceKind(decoded))
        {
            return Quarantine(
                entry, stampedVersion, 11, InstanceQuarantineReason.StackNotInstanceable, sink, ref deciding);
        }

        // Check 12, the one TOLERATED failure, and it is tolerated by contract. It is counted and it
        // changes nothing.
        bool overCap = content.IsOverCap(entry.DefinitionId, entry.Count);
        InstanceValidationFinding overCapFinding = default;
        if (overCap)
        {
            overCapFinding = Policy(
                entry,
                stampedVersion,
                InstanceValidationFinding.OverCapCheck,
                InstanceValidationReason.OverCap);
            sink?.Add(overCapFinding);
        }

        // Check 13. NOT a quarantine and not a fourth outcome: the bytes are not wrapped, the entry still
        // decodes, the container still loads, and what changes is the presentation and the refusals that
        // ride with it.
        if (retired)
        {
            InstanceValidationFinding retiredFinding = Policy(
                entry,
                stampedVersion,
                InstanceValidationFinding.RetiredCheck,
                InstanceValidationReason.DefinitionRetired);
            sink?.Add(retiredFinding);
            deciding = retiredFinding;
        }
        else if (overCap)
        {
            deciding = overCapFinding;
        }

        return InstanceValidationOutcome.Valid;
    }

    /// <summary>
    /// Check 10, which is the one answer a single entry cannot give. EVERY entry sharing a duplicated id
    /// quarantines, the first included: which of them is the original is unknowable from the bytes, and
    /// leaving one usable would be choosing arbitrarily in favour of whoever duplicated it (spec 15.1).
    /// An entry that already quarantined is not counted twice.
    /// </summary>
    static void DuplicateInstanceIds(
        ReadOnlySpan<PageEntry> entries, int stampedVersion, List<InstanceValidationFinding> findings)
    {
        var counts = new Dictionary<long, int>();
        foreach (PageEntry entry in entries)
        {
            // Instance id 0 is what every plain stack carries, so it is not an identity and cannot collide.
            if (entry.InstanceId == 0)
            {
                continue;
            }

            counts.TryGetValue(entry.InstanceId, out int held);
            counts[entry.InstanceId] = held + 1;
        }

        var quarantined = new HashSet<int>();
        foreach (InstanceValidationFinding finding in findings)
        {
            if (finding.IsQuarantine)
            {
                quarantined.Add(finding.Slot);
            }
        }

        foreach (PageEntry entry in entries)
        {
            if (entry.InstanceId == 0
                || counts[entry.InstanceId] < 2
                || quarantined.Contains(entry.Slot))
            {
                continue;
            }

            findings.Add(new InstanceValidationFinding(
                entry.Slot,
                entry.DefinitionId,
                entry.InstanceId,
                stampedVersion,
                10,
                InstanceQuarantineReason.InstanceIdDuplicate,
                InstanceValidationOutcome.Quarantined));
        }
    }

    /// <summary>Whether the payload carries kind 5 or kind 132, the two fields that are per INSTANCE.</summary>
    static bool CarriesPerInstanceKind(ReadOnlySpan<PayloadField> fields)
    {
        foreach (PayloadField field in fields)
        {
            if (field.Kind is InstancePropertyKind.Durability or InstancePropertyKind.Sockets)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The spec 12.2 row a decoder token belongs to, which is spec 12.4's third column read the other way.
    /// <c>varint-overflow</c> is raised under either check 1 or check 3 and is reported as the canonical
    /// form rule it breaks.
    /// </summary>
    static int CheckForToken(string? token) => token switch
    {
        InstancePayloadReason.KindOutOfOrder => 1,
        InstancePayloadReason.KindDuplicate => 1,
        InstancePayloadReason.VarintNotMinimal => 1,
        InstancePayloadReason.VarintOverflow => 1,
        InstancePayloadReason.FieldTruncated => 2,
        InstancePayloadReason.SocketNesting => 4,
        InstancePayloadReason.PayloadTooLong => 5,
        _ => 3,
    };

    static void ReportStoredQuarantine(
        ReadOnlySpan<byte> wrapper,
        in PageEntry entry,
        int pageStamp,
        List<InstanceValidationFinding> findings)
    {
        if (!QuarantineWrapper.TryUnwrap(wrapper, out _, out string? reason, out int stampedVersion))
        {
            reason = InstancePayloadReason.FieldMalformed;
            stampedVersion = pageStamp;
        }

        // A wrapper stores the durable reason rather than the check number. Where two checks share a token,
        // report the first row that owns it, matching CheckForToken's treatment of varint-overflow.
        int check = reason switch
        {
            InstanceQuarantineReason.UnknownDefinition => 6,
            InstanceQuarantineReason.UnknownContentReference => 7,
            InstanceQuarantineReason.InstanceIdMissing => 9,
            InstanceQuarantineReason.InstanceIdDuplicate => 10,
            InstanceQuarantineReason.StackNotInstanceable => 11,
            _ => CheckForToken(reason),
        };
        findings.Add(new InstanceValidationFinding(
            entry.Slot,
            entry.DefinitionId,
            entry.InstanceId,
            stampedVersion,
            check,
            reason,
            InstanceValidationOutcome.Quarantined));
    }

    static InstanceValidationOutcome Quarantine(
        in PageEntry entry,
        int stampedVersion,
        int check,
        string reason,
        List<InstanceValidationFinding>? sink,
        ref InstanceValidationFinding deciding)
    {
        var finding = new InstanceValidationFinding(
            entry.Slot,
            entry.DefinitionId,
            entry.InstanceId,
            stampedVersion,
            check,
            reason,
            InstanceValidationOutcome.Quarantined);
        sink?.Add(finding);
        deciding = finding;
        return InstanceValidationOutcome.Quarantined;
    }

    static InstanceValidationFinding Policy(in PageEntry entry, int stampedVersion, int check, string reason)
        => new(
            entry.Slot,
            entry.DefinitionId,
            entry.InstanceId,
            stampedVersion,
            check,
            reason,
            InstanceValidationOutcome.Valid);

    static ReadOnlySpan<byte> Window(ReadOnlySpan<byte> page, in PageEntry entry)
    {
        if (entry.PayloadStart < 0
            || entry.PayloadLength < 0
            || entry.PayloadStart + (long)entry.PayloadLength > page.Length)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Slot {entry.Slot} declares payload bytes {entry.PayloadStart} to {entry.PayloadStart + entry.PayloadLength} and the page holds {page.Length}. The entries were decoded from a different page."),
                nameof(page));
        }

        return page.Slice(entry.PayloadStart, entry.PayloadLength);
    }
}
