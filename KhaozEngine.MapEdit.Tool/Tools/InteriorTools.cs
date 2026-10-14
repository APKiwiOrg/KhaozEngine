using System.ComponentModel;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Standalone interior and roof wire adapters using the shared native commands.</summary>
[McpServerToolType]
public sealed class InteriorTools(MutationService mutation, MapEditSession session)
{
    [McpServerTool(Name = "interior_volume_add"), Description("Adds an absent standalone prism domain with authored parent, storey and vertical bounds in metres.")]
    public MapPrefabMutationResult InteriorVolumeAdd(string domainJson) => ToolGuard.Guard(() => SetDomain(domainJson, false));

    [McpServerTool(Name = "interior_volume_set"), Description("Replaces a present standalone prism domain without changing its record identity.")]
    public MapPrefabMutationResult InteriorVolumeSet(string domainJson) => ToolGuard.Guard(() => SetDomain(domainJson, true));

    MapPrefabMutationResult SetDomain(string json, bool requirePresent) => session.WithNativeAssets(c =>
    {
        var domain = MapPrefabJson.ReadDomain(json);
        bool present = c.Document.Surfaces.AllRecords().OfType<MapPrismDomain>().Any(d => d.Id == domain.Id);
        if (present != requirePresent) throw new MapDocumentException(requirePresent ? "No standalone interior with that ID." : "An interior with that ID already exists.");
        if (!requirePresent && c.Document.Surfaces.AllRecords().OfType<MapPrismDomain>().Any(d => d.Space.Id == domain.Space.Id))
            throw new MapDocumentException("The interior space already owns a prism.");
        return mutation.InteriorApply(new MapSetInterior(domain));
    });

    [McpServerTool(Name = "interior_volume_remove"), Description("Removes a standalone interior and its room by space ID.")]
    public MapPrefabMutationResult InteriorVolumeRemove(string spaceId) => ToolGuard.Guard(() => mutation.InteriorApply(new MapRemoveInterior(spaceId)));

    [McpServerTool(Name = "roof_link_set"), Description("Sets an explicit roof link and attached room/group inventory. This changes concealment intent, not collision.")]
    public MapPrefabMutationResult RoofLinkSet(string linkJson) => ToolGuard.Guard(() => mutation.InteriorApply(new MapSetRoofLink(MapPrefabJson.ReadRoofLink(linkJson))));
}
