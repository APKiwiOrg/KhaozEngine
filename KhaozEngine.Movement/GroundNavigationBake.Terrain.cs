using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigationBake
{
    readonly PhysicsNavDescentEvidence? _terrainEvidence;

    static IEnumerable<NavLink> TerrainCandidates(GroundNavigation navigation)
    {
        foreach (var link in NavLayerLinks.EnumerateGrounded(navigation.Space.Layers, navigation.Identity.Tuning.StepHeight)) yield return link;
        var terrain = navigation.Terrain ?? throw new InvalidOperationException("Terrain navigation has no frozen evidence.");
        foreach (var link in ContactTerrainDropCandidates.Enumerate(navigation, terrain.Evidence, terrain.Sources,
            navigation.Identity.Tuning, navigation.MaxDropMetres)) yield return link;
    }

    byte[] EncodeTerrainPayload()
    {
        var writer = new NavBakeWriter();
        WriteCapture(writer);
        var evidence = TerrainEvidenceWriter.Encode(_terrainEvidence!, _columns);
        writer.WriteInt32(evidence.Length); writer.WriteBytes(evidence);
        for (int i = 0; i < _profiles.Length; i++)
        {
            var profile = _profiles[i]; var data = new NavBakeWriter(); WriteProfile(data, profile);
            if (profile.AllowTerrainDescents && !SameLinks(TerrainCandidates(profile), profile.Space.Links))
                throw new InvalidOperationException("The frozen terrain candidate order changed.");
            var digest = TerrainWorkload.Digest(_identity, evidence, _names[i], data.WrittenSpan,
                profile.AllowTerrainDescents ? TerrainCandidates(profile) : profile.Space.Links, out int count);
            if (count != profile.Space.Links.Count) throw new InvalidOperationException("The frozen terrain workload changed.");
            writer.WriteInt32(data.Length); writer.WriteBytes(data.WrittenSpan); writer.WriteBytes(digest);
        }
        return writer.ToArray();
    }

    static PhysicsNavDescentEvidence? ContainerTerrain(PhysicsNavBake capture, GroundNavigation[] profiles)
    {
        if (!capture.Options.CaptureTerrainDescentEvidence) return null;
        var union = TerrainEvidenceUnion.Build(capture.Options, capture.Columns,
            profiles.Where(p => p.AllowTerrainDescents).Select(p => p.Terrain!.Evidence)).RequireDependencies();
        foreach (var profile in profiles)
            if (profile.AllowTerrainDescents) profile.RebindTerrainEvidence(union);
        return union;
    }
}
