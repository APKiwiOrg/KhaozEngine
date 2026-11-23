using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class LiftBindCostDiagnostics
{
    const float FootprintGridSpacing = 1f / 128f;

    static Cost MeasureFootprintBinds(OutfitRig rig, out int bodyTriangleCount)
    {
        // 36 by 22 quads give the 1,584 body triangles and 101,376 cells used by the shipped body.
        const int bodyColumns = 37, bodyRows = 23;
        const string bodyKey = "footprint-body.json";
        uint[] indices = GridTriangles(bodyColumns, bodyRows).Select(index => (uint)index).ToArray();
        Vector3[] positions = FootprintGridPositions(bodyColumns, bodyRows, 0);
        var bodyGeometry = new CoverageClaimMesh(positions, indices);
        bodyTriangleCount = indices.Length / 3;
        var vertices = positions.Select(position => new SkinnedVertex
        {
            Position = position,
            Normal = Vector3.UnitZ,
            Color = Vector4.One,
            BoneWeights = Vector4.UnitX,
        }).ToArray();
        var mesh = new SkinnedGltfMesh(vertices, indices, rig.Body.InverseBind, rig.Palette, rig.Skeleton);
        SkinnedMeshHandle handle = rig.Scene.LoadSkinnedMesh(mesh);
        string hash = rig.BodyMap(bodyKey, OutfitRig.Rig, new RegionRun(0, bodyTriangleCount));
        LoadedBodyRegions body = rig.Cache.GetBodyRegions(bodyKey, hash, OutfitRig.Rig, [handle], [mesh]);

        LoadedAttachmentSet armour = LoadFootprintPanel(rig, body, bodyGeometry, "footprint-shell", Bearing, AttachmentLayer.Armour);
        var inputs = new BoundAttachmentSet[WearerCount][];
        for (int wearer = 0; wearer < inputs.Length; wearer++)
        {
            float height = .015625f + wearer / 4096f;
            LoadedAttachmentSet garment = LoadFootprintPanel(rig, body, bodyGeometry,
                $"footprint-liner-{wearer}", height, AttachmentLayer.Garment);
            inputs[wearer] = [rig.Bind(garment), rig.Bind(armour)];
        }

        var outfits = new BoundOutfit[inputs.Length];
        try
        {
            Cost cost = MeasureBinds(rig.Cache, body, inputs, outfits);
            var views = new HashSet<SkinnedMeshHandle>();
            foreach (BoundOutfit outfit in outfits)
            {
                Assert.True(outfit.FootprintApplied(0));
                Assert.True(outfit.FootprintApplied(1));
                SkinnedMeshHandle view = Assert.Single(rig.Draws(outfit, 1)).Skinned.Mesh;
                Assert.True(rig.Scene.IsSkinnedMeshView(view));
                Assert.True(views.Add(view), "Each garment height must produce a distinct displaced armour view.");
            }
            return cost;
        }
        finally
        {
            foreach (BoundOutfit outfit in outfits) outfit?.Dispose();
        }
    }

    static LoadedAttachmentSet LoadFootprintPanel(OutfitRig rig, LoadedBodyRegions body, CoverageClaimMesh bodyGeometry,
        string key, float height, AttachmentLayer layer)
    {
        const uint partId = 1;
        ushort[] triangles = GridTriangles(PanelSide, PanelSide);
        Vector3[] positions = FootprintGridPositions(PanelSide, PanelSide, height);
        SkinnedAttachmentGlbFixture panel = OutfitRig.GarmentFixture();
        panel.SetUniformSkinning(0, positions.Length, new Vector4(0, 1, 0, 0), new Vector4(.5f, .5f, 0, 0));
        panel.SetIndexedMaterialParts(triangles, 1);
        panel.SetPositions(0, positions);
        bool lifts = layer == AttachmentLayer.Armour;
        if (lifts)
        {
            var directions = new Vector3[positions.Length];
            var bearings = new float[positions.Length];
            Array.Fill(directions, Vector3.UnitZ);
            Array.Fill(bearings, Bearing);
            panel.SetLiftAttributes(0, directions, bearings);
        }
        var part = new SkinnedAttachmentPartDefinition(partId, rig.Source.Mesh(key + ".glb", panel.Build()))
        {
            Regions = [new("alpha", triangles.Length / 3)],
            Lift = lifts ? new(Clearance) : null,
        };
        var definition = new AttachmentSetDefinition([part], layer, [], [], [new("alpha", height)]);
        var piece = new CoverageClaimMesh(positions, triangles.Select(index => (uint)index).ToArray()) { PartId = partId };
        LiftFootprint footprint = LiftFootprintMeasure.Measure([bodyGeometry], body.Map, definition, [piece]);
        return rig.Load(key + ".json", rig.WithFootprint(key + ".khlf", definition, footprint));
    }

    static Vector3[] FootprintGridPositions(int columns, int rows, float height)
    {
        var positions = new Vector3[columns * rows];
        for (int index = 0; index < positions.Length; index++)
            positions[index] = new Vector3(index % columns * FootprintGridSpacing, index / columns * FootprintGridSpacing, height);
        return positions;
    }

    static ushort[] GridTriangles(int columns, int rows)
    {
        var triangles = new ushort[(columns - 1) * (rows - 1) * 6];
        int index = 0;
        for (int row = 0; row < rows - 1; row++)
            for (int column = 0; column < columns - 1; column++)
            {
                int start = row * columns + column;
                triangles[index++] = (ushort)start;
                triangles[index++] = (ushort)(start + 1);
                triangles[index++] = (ushort)(start + columns);
                triangles[index++] = (ushort)(start + 1);
                triangles[index++] = (ushort)(start + columns + 1);
                triangles[index++] = (ushort)(start + columns);
            }
        return triangles;
    }
}
