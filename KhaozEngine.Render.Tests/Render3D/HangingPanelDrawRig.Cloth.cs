using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Simulation;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal sealed partial class HangingPanelDrawRig
{
    private HangingPanelCut? clothCut;
    private ClothDrapeFixture? frozenFixture;
    private BoundClothMaterial? bareCloth;
    private BoundClothMaterial? fittedCloth;
    private WornMotion? crowd;
    private WornMotionBatch? wearerBatch;
    private WornClothSelection clothRequest;
    private Matrix4x4[]? frozenHost;
    private Vector3[]? detailed;
    private Matrix4x4[]? composedCloth;
    private bool firstClothPose = true;

    internal WornClothStatus CurrentClothStatus => motion!.ClothStatus;
    internal bool CurrentClothPose { get; private set; }
    internal Vector3 MarkedFittedOffset { get; private set; }
    internal Vector3 MarkedBareOffset { get; private set; }

    private void InitializeCloth(ClothDrapeFixture fixture, HangingPanelCut cut, BoundClothMaterial bare)
    {
        var surface = outfit.ClothSurface(garmentIndex, 1)!;
        clothCut = cut;
        frozenFixture = fixture;
        bareCloth = bare;
        fittedCloth = surface.Materials[0];
        foreach (var entry in bare.Embeddings)
            if (entry.VertexIndex == cut.Probes.MarkedPatch) MarkedBareOffset = entry.FittedOffset;
        foreach (var entry in surface.Materials[0].Embeddings)
            if (entry.VertexIndex == cut.Probes.MarkedPatch) MarkedFittedOffset = entry.FittedOffset;
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        clothRequest = new(slot, garmentIndex, 1);
        crowd = outfit.CreateMotion(null);
        wearerBatch = new(new SingleThreadedJobScheduler());
        frozenHost = (Matrix4x4[])palette.Clone();
        detailed = new Vector3[surface.Materials[0].Vertices.Length];
        composedCloth = new Matrix4x4[host.Skeleton!.BoneCount + HangingPanelCut.Columns * JointsPerGuide];
        Assert.Equal(fixture.Fitted.Length, surface.FittedNodes.Length);
        for (int i = 0; i < fixture.Fitted.Length; i++)
            Assert.InRange(Vector3.Distance(fixture.Fitted[i], surface.FittedNodes[i]), 0, .00001f);
    }

    internal PresentedPanel PresentCloth(in HangingPanelPose pose, float elapsed, bool batch)
    {
        SetPose(pose);
        if (batch)
        {
            wearerBatch!.Clear();
            wearerBatch.Add(crowd!, rig.Skeleton, frozenHost!, pose.Wearer, elapsed, MotionLevel.Held);
            wearerBatch.Add(motion!, rig.Skeleton, palette, pose.Wearer, elapsed, MotionLevel.Simulated, clothRequest);
            wearerBatch.Step();
        }
        else
            motion!.Step(rig.Skeleton, palette, pose.Wearer, elapsed, MotionLevel.Simulated, clothRequest);

        Assert.True(outfit.TryGetClothPresentation(garmentIndex, motion!, 1, out var shown),
            $"Cloth display is missing at phase {pose.PhaseSeconds:R}. {motion!.ClothStatus}");
        CurrentClothPose = shown.IsCurrentPose;
        AttachmentDraw draw = Write(garmentIndex, shown.Model);
        VerifySupportPose(pose);
        if (firstClothPose)
        {
            Assert.True(shown.Surface.Materials[0].VisibleIndices.AsSpan().SequenceEqual(garment.SourceMesh.Indices32));
            Assert.Equal(draw.Skinned.Model, shown.Model);
            firstClothPose = false;
        }
        return ReadSurface(shown, shown.Surface.Materials[0]);
    }

    internal PresentedPanel PresentWithoutOutfitLift()
    {
        Assert.True(outfit.TryGetClothPresentation(garmentIndex, motion!, 1, out var shown));
        return ReadSurface(shown, bareCloth!);
    }

    private PresentedPanel ReadSurface(scoped in WornClothPresentation shown, BoundClothMaterial material)
    {
        var cut = clothCut!;
        var paletteOutput = composedCloth!.AsSpan(0, shown.GarmentPalette.Length);
        Assert.True(Matrix4x4.Invert(shown.RelativeModel, out Matrix4x4 toModel));
        var fitted = garment.SourceMesh;
        for (int i = 0; i < paletteOutput.Length; i++)
            paletteOutput[i] = SkinningMath.Compose(shown.GarmentPalette[i], fitted.InverseBind[i]);
        for (int i = 0; i < material.Vertices.Length; i++)
            detailed![i] = SkinningMath.SkinVertex(material.Vertices[i], paletteOutput).Position;

        foreach (var embedding in material.Embeddings)
        {
            ClothTriangle face = shown.Surface.Rig.Triangles[embedding.Triangle];
            if (!ClothFittedFrame.TryCreate(face, shown.Nodes, out var frame))
                throw new InvalidOperationException("Presented material triangle has no frame.");
            Vector3 offset = embedding.FittedOffset;
            Vector3 worldRelative = face.Blend(shown.Nodes, embedding.Barycentric) +
                offset.X * frame.U + offset.Y * frame.V + offset.Z * frame.Normal;
            detailed![embedding.VertexIndex] = Vector3.Transform(worldRelative, toModel);
        }
        return new(detailed, cut.Faces, cut.MaterialTriangles, cut.Probes,
            CurrentClothStatus.Fallback == WornClothFallback.None ? 0 : 1);
    }

    internal PresentedPanel PresentFrozenProxyWitness(in HangingPanelPose pose)
    {
        Assert.True(outfit.TryGetClothPresentation(garmentIndex, motion!, 1, out var shown));
        Assert.Equal(Matrix4x4.Identity, shown.RelativeModel);
        return frozenFixture!.Present(shown.Nodes, pose, CurrentClothStatus.Fallback == WornClothFallback.None ? 0 : 1);
    }

    internal LiningWitness ClosestLiningWitness(Vector3 marker)
    {
        ReadOnlySpan<Vector3> positions = detailed;
        PanelTriangle closest = default;
        Vector3 point = default;
        float squared = float.PositiveInfinity;
        foreach (PanelTriangle face in clothCut!.Faces)
        {
            if (face.Face != PanelFace.Lining) continue;
            Vector3 candidate = PanelGeometry.ClosestPoint(marker, positions[face.A], positions[face.B], positions[face.C]);
            float distance = Vector3.DistanceSquared(marker, candidate);
            if (distance >= squared) continue;
            (squared, point, closest) = (distance, candidate, face);
        }
        return new(MathF.Sqrt(squared), Coordinates(marker), Coordinates(point),
            [Describe(closest.A), Describe(closest.B), Describe(closest.C)]);

        LiningVertex Describe(int vertex)
        {
            var fitted = Find(fittedCloth!, vertex);
            var bare = Find(bareCloth!, vertex);
            return new(vertex, fitted.Triangle, fitted.SignedOffset, Coordinates(fitted.Barycentric),
                Coordinates(fitted.FittedOffset), Coordinates(bare.FittedOffset));
        }

        static BoundClothEmbedding Find(BoundClothMaterial material, int vertex)
        {
            foreach (var entry in material.Embeddings)
                if (entry.VertexIndex == vertex) return entry;
            return default;
        }

        static float[] Coordinates(Vector3 vector) => [vector.X, vector.Y, vector.Z];
    }

    internal sealed record LiningWitness(float Distance, float[] Marker, float[] Point, LiningVertex[] Vertices);
    internal sealed record LiningVertex(int Vertex, int Triangle, float AuthoredOffset, float[] Weights, float[] Fitted, float[] Bare);

    internal void VerifyOriginalFallback(HangingPanelDrawRig original, in HangingPanelPose neutral)
    {
        var reference = original.garment.SourceMesh;
        var candidate = garment.SourceMesh;
        Assert.Equal(reference.Indices32, candidate.Indices32);
        for (int vertex = 0; vertex < reference.Vertices.Length; vertex++)
        {
            Assert.Equal(reference.Vertices[vertex].Position, candidate.Vertices[vertex].Position);
            Assert.Equal(reference.Vertices[vertex].BoneIndices, candidate.Vertices[vertex].BoneIndices);
            Assert.Equal(reference.Vertices[vertex].BoneWeights, candidate.Vertices[vertex].BoneWeights);
        }
        var oldPanel = original.Present(neutral);
        var current = Present(neutral);
        Assert.True(oldPanel.Vertices.SequenceEqual(current.Vertices[..oldPanel.Vertices.Length]));
        motion!.Reset();
    }

    private (BoundAttachmentSet Set, string MeshDigest, string DefinitionDigest) WearClothPanel(SkinnedAttachmentGlbFixture glb,
        HangingPanelCut cut, in PanelDrape drape, ChainMotionDefinition fallback, ClothDrapeFixture fixture)
    {
        Vector3[] dense = cut.Fit(drape);
        var rigged = fixture.Rig;
        int pinCount = rigged.Pins.Length;
        var positions = new Vector3[dense.Length + fixture.Fitted.Length - pinCount];
        dense.CopyTo(positions, 0);
        fixture.Fitted.AsSpan(pinCount).CopyTo(positions.AsSpan(dense.Length));
        var liftDirections = new Vector3[positions.Length];
        var liftBearings = new float[positions.Length];
        Array.Fill(liftDirections, Vector3.UnitZ);
        Array.Fill(liftBearings, LowerStandoff + LiftClearance);
        glb.SetUniformSkinning(0, positions.Length, Vector4.Zero, Vector4.UnitX);
        var render = new List<ClothRenderEmbedding>();
        for (int vertex = 0; vertex < dense.Length; vertex++)
        {
            int source = vertex % HangingPanelCut.VerticesPerSkin;
            int row = source / HangingPanelCut.Columns;
            if (row == 0) continue;
            positions[vertex] -= LiftClearance * Vector3.UnitZ;
            liftBearings[vertex] = LowerStandoff;
            (int preferred, _) = fixture.DenseBinding(source);
            var (triangle, weights, signed) = AttachSource(fixture.Fitted, rigged.Triangles, positions[vertex], preferred);
            render.Add(new(new(0, 0, vertex), triangle, weights, signed));
            WeightGuide(vertex, row, source % HangingPanelCut.Columns);
        }
        var reverse = new ClothSourcePoint[fixture.Fitted.Length];
        var pins = new ClothPinSource[pinCount];
        for (int node = 0; node < reverse.Length; node++)
        {
            if (node < pinCount)
            {
                reverse[node] = SeamSource(cut, dense, fixture.Fitted[node]);
                pins[node] = new(node, reverse[node]);
                continue;
            }

            // These vertices preserve exact fitted guide samples without altering any visible index.
            int vertex = dense.Length + node - pinCount;
            reverse[node] = new(0, 0, vertex);
            var (triangle, weights) = NodeEmbedding(rigged.Triangles, node);
            render.Add(new(new(0, 0, vertex), triangle, weights, 0));
            int guide = (int)MathF.Round((node % ClothDrapeFixture.Columns) *
                ((HangingPanelCut.Columns - 1f) / (ClothDrapeFixture.Columns - 1)));
            WeightGuide(vertex, ClothDrapeFixture.DenseRow(node), guide);
        }
        glb.SetPositions(0, positions);
        glb.SetLiftAttributes(0, liftDirections, liftBearings);

        glb.SetIndexedMaterialParts(Indices(cut.Faces), 1);
        var mesh = rig.Source.Mesh("hanging-cloth-panel.glb", glb.Build());
        var sidecar = new ClothSurfaceFile(mesh.Sha256, positions.Length, cut.Faces.Length, rigged.Material,
            [.. rigged.RestPositions], [.. rigged.MaterialCoordinates], [.. rigged.Triangles], [],
            [.. pins], [.. render], [.. reverse]);
        const string sidecarKey = "hanging-cloth-panel.kecs";
        string sha = rig.Source.Add(sidecarKey, ClothSurfaceJson.Write(sidecar));
        var part = new SkinnedAttachmentPartDefinition(1, mesh)
        {
            Regions = [new NamedRegionRun("alpha", cut.Faces.Length)],
            Motion = new ClothMotionDefinition(new(sidecarKey, sha), fallback),
            Lift = new(LiftClearance),
        };
        var definition = new AttachmentSetDefinition([part], AttachmentLayer.Outerwear, []);
        return (Wear("hanging-cloth-panel.json", definition), mesh.Sha256, definition.SemanticSha256);

        void WeightGuide(int vertex, int row, int col)
        {
            int joints = glb.Primitives[0]!["attributes"]!["JOINTS_0"]!.GetValue<int>();
            int weights = glb.Primitives[0]!["attributes"]!["WEIGHTS_0"]!.GetValue<int>();
            float along = row * JointsPerGuide / (HangingPanelCut.Rows - 1f);
            int upper = Math.Min((int)along, JointsPerGuide - 1);
            int lower = Math.Min(upper + 1, JointsPerGuide - 1);
            float amount = upper == lower ? 0 : along - upper;
            glb.SetElement(joints, vertex, GuideBone(col, upper), GuideBone(col, lower), 0, 0);
            glb.SetElement(weights, vertex, 1 - amount, amount, 0, 0);
        }
    }

    private static (int Triangle, Vector3 Weights, float Signed) AttachSource(ReadOnlySpan<Vector3> fitted,
        ReadOnlySpan<ClothTriangle> triangles, Vector3 point, int preferred)
    {
        float tolerance = ClothSurfaceFile.RoundTripTolerance;
        var winner = (Triangle: preferred, Weights: Vector3.Zero, Signed: 0f);
        float closest = float.PositiveInfinity;
        for (int i = 0; i < triangles.Length; i++)
        {
            int triangle = (preferred + i) % triangles.Length;
            var face = triangles[triangle];
            var (weights, signed, edgeError) = SourceWeights(fitted[face.A], fitted[face.B], fitted[face.C], point);
            if (edgeError > tolerance) continue;
            float rank = MathF.Abs(signed) + edgeError;
            if (rank >= closest) continue;
            winner = (triangle, weights, signed);
            closest = rank;
        }
        Assert.True(float.IsFinite(closest), $"Fitted source position {point} has no material projection.");
        return winner;
    }

    private static ClothSourcePoint SeamSource(HangingPanelCut cut, Vector3[] positions, Vector3 pin)
    {
        for (int triangle = 0; triangle < cut.Faces.Length; triangle++)
        {
            var face = cut.Faces[triangle];
            if (face.A % HangingPanelCut.VerticesPerSkin >= HangingPanelCut.Columns ||
                face.B % HangingPanelCut.VerticesPerSkin >= HangingPanelCut.Columns ||
                face.C % HangingPanelCut.VerticesPerSkin >= HangingPanelCut.Columns) continue;
            var (weights, signed, edgeError) = SourceWeights(positions[face.A], positions[face.B], positions[face.C], pin);
            if (MathF.Abs(signed) + edgeError < .000001f)
                return new(0, 0, triangle, weights);
        }
        throw new InvalidOperationException("The pin is outside the fixed seam rim.");
    }

    private static (Vector3 Weights, float Signed, float EdgeError) SourceWeights(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        Vector3 normal = Vector3.Cross(ab, ac);
        float areaSquared = normal.LengthSquared();
        float v = Vector3.Dot(Vector3.Cross(ap, ac), normal) / areaSquared;
        float w = Vector3.Dot(Vector3.Cross(ab, ap), normal) / areaSquared;
        Vector3 weights = Vector3.Max(Vector3.Zero, new Vector3(1 - v - w, v, w));
        weights /= weights.X + weights.Y + weights.Z;
        normal /= MathF.Sqrt(areaSquared);
        float signed = Vector3.Dot(ap, normal);
        return (weights, signed, Vector3.Distance(p - signed * normal, weights.X * a + weights.Y * b + weights.Z * c));
    }

    private static (int Triangle, Vector3 Weights) NodeEmbedding(ReadOnlySpan<ClothTriangle> triangles, int node)
    {
        for (int index = 0; index < triangles.Length; index++)
        {
            ClothTriangle triangle = triangles[index];
            if (triangle.A == node) return (index, Vector3.UnitX);
            if (triangle.B == node) return (index, Vector3.UnitY);
            if (triangle.C == node) return (index, Vector3.UnitZ);
        }
        throw new InvalidOperationException("The node is absent from the material surface.");
    }
}
