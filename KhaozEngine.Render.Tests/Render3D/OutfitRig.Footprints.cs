using System.Linq;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;

namespace KhaozEngine.Tests.Render3D;

internal sealed partial class OutfitRig
{
    internal LiftFootprint MeasureFootprint(LoadedBodyRegions body, AttachmentSetDefinition definition,
        params CoverageClaimMesh[] piece) =>
        LiftFootprintMeasure.Measure(
            [new CoverageClaimMesh(Body.Vertices.Select(vertex => vertex.Position).ToArray(), Body.Indices32)],
            body.Map, definition, piece);

    internal AttachmentSetDefinition WithFootprint(string key, AttachmentSetDefinition definition, LiftFootprint footprint) =>
        new(definition.Parts, definition.Layer, definition.Covers, definition.Colliders, definition.Standoff,
            new LiftFootprintRef(key, Source.Add(key, LiftFootprintFile.Write(footprint))));
}
