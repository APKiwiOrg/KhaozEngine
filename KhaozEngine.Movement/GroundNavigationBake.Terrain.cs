using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigationBake
{
    readonly PhysicsNavDescentEvidence? _terrainEvidence;

    byte[] EncodeTerrainPayload()
    {
        var writer = new NavBakeWriter();
        WriteCapture(writer);
        var evidence = TerrainEvidenceWriter.Encode(_terrainEvidence!, _columns);
        writer.WriteInt32(evidence.Length); writer.WriteBytes(evidence);
        for (int i = 0; i < _profiles.Length; i++)
        {
            var profile = _profiles[i]; var data = new NavBakeWriter(); WriteProfile(data, profile);
            var facts = new NavBakeWriter();
            facts.WriteSingle(profile.Terrain?.SlopeCos ?? 0);
            facts.WriteUInt64(BitConverter.DoubleToUInt64Bits(profile.Footprint.SurfaceReach));
            byte[] endpoints = TerrainCandidateCodec.Encode(profile.Space.Links, _columns.Width, _columns.Height);
            facts.WriteInt32(endpoints.Length); facts.WriteBytes(endpoints);
            var routes = new byte[BitsetLength(profile.Space.Links.Count)];
            for (int c = 0; c < profile.Space.Links.Count; c++)
                if (profile.Terrain?.Route(profile.Space.Links[c]) == ContactDropRoute.Terrain) routes[c >> 3] |= (byte)(1 << (c & 7));
            facts.WriteBytes(routes);
            var digest = TerrainWorkload.Digest(_identity, evidence, _names[i], data.WrittenSpan, facts.WrittenSpan);
            writer.WriteInt32(data.Length); writer.WriteBytes(data.WrittenSpan);
            writer.WriteInt32(facts.Length); writer.WriteBytes(facts.WrittenSpan); writer.WriteBytes(digest);
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
