using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The three primitives of spec 10.2 whose subject is the SOCKET LIST: 6 <c>AddSocket</c>, 7 <c>Socket</c>
/// and 8 <c>Unsocket</c>.
/// <para>
/// <b><c>Socket</c> and <c>Unsocket</c> are the only primitives that touch TWO slots</b>, which is what
/// makes them the only ones that can turn a craft into a two page commit (spec 5.6). It shows in the
/// signatures rather than in a comment: they are the only two carrying a contained item's definition, its
/// instance id and its payload, and every other primitive's whole world is the working copy.
/// </para>
/// <para>
/// <b><c>Socket</c> KEEPS the moved item's instance id.</b> The socket entry carries it (contracts 9.5), so
/// unsocketing RESTORES that identity rather than minting a new one, and an item duplicated by an exploit
/// stays traceable to the instance it was copied from. Nothing here can allocate one anyway, because the
/// working copy holds no allocator.
/// </para>
/// <para>
/// <b>Socket ORDER is authored and never sorted</b>, unlike the affix list, so two otherwise identical
/// items whose gems sit in different sockets do NOT stack, which is correct because they are different
/// items.
/// </para>
/// </summary>
public static partial class CraftPrimitives
{
    /// <summary>
    /// Primitive 6. Appends one EMPTY socket to kind 132, keeping the authored order.
    /// <para>
    /// The base's own <c>socket_max</c> is NOT consulted, and that is deliberate: it is an authoring number
    /// the generator seats at step 11, while a craft's ceiling is the payload budget and whatever
    /// <c>SocketCountBetween</c> guard the currency authored. The working copy carries a payload and a
    /// definition id, and a rule read off the base here would be a second cap nobody could see in the
    /// currency row.
    /// </para>
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="socketTypeId">The socket type, or 0 for no restriction.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? AddSocket(ref CraftWorkingCopy copy, int socketTypeId)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (socketTypeId < 0)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, socketTypeId));
        }

        if (socketTypeId > 0
            && !InstanceContentChecks.IsLive(copy.Snapshot, InstanceContentTypeIds.SocketTypeTypeId, socketTypeId))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ContentRowMissing, socketTypeId));
        }

        int count = copy.SocketCount;
        var sockets = new InstanceSocket[count + 1];
        _ = copy.ReadSockets(sockets);
        sockets[count] = new InstanceSocket(socketTypeId, 0, 0, default);
        return copy.SetSockets(sockets) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 7. Moves an item INTO a socket, keeping its instance id. The three refusals come in the
    /// order spec 8.7 fixes: the tag rules here, then the nested budget and the one level limit at the
    /// working copy's socket write door, which is where a game operation meets them too.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="socketIndex">Which socket, in authored order.</param>
    /// <param name="containedDefinitionId">The moved item's definition, which must be a live <c>item</c> row.</param>
    /// <param name="containedInstanceId">Its instance id, KEPT verbatim, or 0 when it has none.</param>
    /// <param name="containedPayload">Its own payload, which may be empty.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? Socket(
        ref CraftWorkingCopy copy,
        int socketIndex,
        int containedDefinitionId,
        ulong containedInstanceId,
        scoped ReadOnlySpan<byte> containedPayload)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        int count = copy.SocketCount;
        if (socketIndex < 0 || socketIndex >= count)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.SocketIndexOutOfRange, socketIndex));
        }

        var sockets = new InstanceSocket[count];
        _ = copy.ReadSockets(sockets);
        if (sockets[socketIndex].ContainedDefinitionId != 0)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.SocketOccupied, socketIndex));
        }

        if (!InstanceContentChecks.IsLive(copy.Snapshot, EngineContentTypes.ItemTypeId, containedDefinitionId))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ContentRowMissing, containedDefinitionId));
        }

        int socketTypeId = sockets[socketIndex].SocketTypeId;
        if (Admits(in copy, socketTypeId, containedDefinitionId) is CraftRefusal rejected)
        {
            return rejected;
        }

        // The nested budget and the one level limit are the WRITE DOOR's, in that order, because a game
        // operation seats its own socket list and would otherwise get neither. This primitive keeps only the
        // tag rules, which are about the item being moved rather than about its bytes.
        sockets[socketIndex] = sockets[socketIndex] with
        {
            ContainedDefinitionId = containedDefinitionId,
            ContainedInstanceId = containedInstanceId,
            Nested = containedPayload.ToArray(),
        };

        return copy.SetSockets(sockets) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 8. Moves the contained item back out, keeping its instance id, and leaves the socket empty
    /// and in place.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="socketIndex">Which socket, in authored order.</param>
    /// <param name="containedDefinitionId">The moved item's definition, when the step applied.</param>
    /// <param name="containedInstanceId">Its instance id, RESTORED rather than minted.</param>
    /// <param name="containedPayload">Its own payload, which may be empty.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? Unsocket(
        ref CraftWorkingCopy copy,
        int socketIndex,
        out int containedDefinitionId,
        out ulong containedInstanceId,
        out byte[] containedPayload)
    {
        containedDefinitionId = 0;
        containedInstanceId = 0;
        containedPayload = [];
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        int count = copy.SocketCount;
        if (socketIndex < 0 || socketIndex >= count)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.SocketIndexOutOfRange, socketIndex));
        }

        var sockets = new InstanceSocket[count];
        _ = copy.ReadSockets(sockets);
        InstanceSocket held = sockets[socketIndex];
        if (held.ContainedDefinitionId == 0)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.SocketEmpty, socketIndex));
        }

        sockets[socketIndex] = held with
        {
            ContainedDefinitionId = 0,
            ContainedInstanceId = 0,
            Nested = default,
        };

        if (!copy.SetSockets(sockets))
        {
            return copy.Refusal;
        }

        containedDefinitionId = held.ContainedDefinitionId;
        containedInstanceId = held.ContainedInstanceId;
        containedPayload = held.Nested.ToArray();
        return null;
    }

    /// <summary>
    /// The socket type's <c>socket_tag_rule</c> rows over the contained item's authored tags. REJECT is
    /// checked FIRST and WINS, and a socket type with NO accept row accepts NOTHING, which is a legal state
    /// rather than an error and is how a decorative socket is authored. A socket type of 0 is the one and
    /// only "no restriction", so nothing anywhere reads an empty accept set as permissive.
    /// </summary>
    static CraftRefusal? Admits(in CraftWorkingCopy copy, int socketTypeId, int containedDefinitionId)
    {
        if (socketTypeId == 0)
        {
            return null;
        }

        var tags = new List<int>();
        if (copy.Snapshot.TryGetRow(new ContentTypeId(EngineContentTypes.ItemTypeId), containedDefinitionId, out ContentRow? item))
        {
            GenerationTagSignature.ReadTags(item, tags);
        }

        bool accepted = false;
        foreach (ContentRow rule in InstanceContentChecks.LiveRows(copy.Snapshot, InstanceContentTypeIds.SocketTagRuleTypeId))
        {
            if (InstanceContentChecks.Number(rule, SocketTagRuleContentType.SocketTypeIdIndex) != socketTypeId)
            {
                continue;
            }

            long tagId = InstanceContentChecks.Number(rule, SocketTagRuleContentType.TagIdIndex) ?? 0;
            if (!tags.Contains((int)tagId))
            {
                continue;
            }

            if (InstanceContentChecks.Number(rule, SocketTagRuleContentType.RuleIndex) == SocketTagRuleContentType.RuleReject)
            {
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.SocketTagRejected, socketTypeId));
            }

            accepted = true;
        }

        return accepted
            ? null
            : copy.Refuse(new CraftRefusal(CraftRefusalKind.SocketTagRejected, socketTypeId));
    }
}
