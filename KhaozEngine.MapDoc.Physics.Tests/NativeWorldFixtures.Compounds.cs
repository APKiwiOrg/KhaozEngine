using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

internal static partial class NativeWorldFixtures
{
    internal static MapBuiltWorld BuildNestedCompound(int levels)
    {
        float[] offsets = new[] { -524288f }.Concat(Enumerable.Repeat(-0.03125f, levels - 2)).Append(524288f).ToArray();
        PhysicsShape shape = new BoxShape(new(0.515625f, 0.5f, 0.5f));
        foreach (float offset in offsets.Reverse())
            shape = new CompoundShape(new[] { new CompoundChild(shape, Pose.At(new(offset, 0, 0))) });
        var source = new NativeWorldAssetSource();
        MapAssetRef mesh = source.Add(SharedMeshId, "mesh:nested-compound");
        MapAssetRef collider = source.Add("nested-compound.collider", ColliderBytes(shape));
        MapAssetRef root = source.Add("nested-compound.assets", new JsonObject
        {
            ["payloadVersion"] = 1,
            // The authored render extent ends before the seam. Only the installed collider adds tile X 1.
            ["assets"] = new JsonArray(Asset("nested-compound", new(-1, -0.5f, -0.5f), new(0.453125f, 0.5f, 0.5f), collider.Id, null)),
            ["resources"] = new JsonArray(Resource(mesh, MapResourceKind.Mesh), Resource(collider, MapResourceKind.Collider))
        }.ToJsonString());
        var assets = MapAssetClosure.Load(new[] { root }, source);
        var document = new MapDocument
        {
            Id = "nested-compound-world",
            TileSize = 64,
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = new() { root },
            Bounds = new() { MinX = 0, MinZ = -64, MaxX = 64, MaxZ = 0 },
            PlayableBounds = new() { MinX = 0, MinZ = -64, MaxX = 64, MaxZ = 0 }
        };
        document.Placements.Add(new()
        {
            Id = "nested-compound",
            Kind = "prop",
            AssetId = "nested-compound",
            X = 63.5f,
            Y = 0,
            Z = 0,
            Scale = 1
        });
        return MapWorldBuilder.Build(document, assets, Options());
    }

    internal static MapBuiltWorld BuildRotationCancellation(bool orientedChild, bool preciseYaw)
    {
        var inner = new CompoundShape(new[] { new CompoundChild(new BoxShape(new(1f / 1024, 0.5f, 1f / 1024)),
            new Pose(Vector3.Zero, orientedChild ? new Quaternion(0, 1, 0, 0) : Quaternion.Identity)) });
        var shape = new CompoundShape(new[] { new CompoundChild(inner, Pose.At(new(655360.25f, 0, 655360.25f))) });
        var source = new NativeWorldAssetSource();
        MapAssetRef mesh = source.Add(SharedMeshId, "mesh:rotation-cancellation");
        MapAssetRef collider = source.Add("rotation-cancellation.collider", ColliderBytes(shape));
        // A visual marker at the root cannot add the missing tile on behalf of the remote collider.
        JsonObject asset = Asset("rotation-cancellation", Vector3.Zero, Vector3.Zero, collider.Id, null);
        MapAssetRef root = source.Add("rotation-cancellation.assets", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(asset),
            ["resources"] = new JsonArray(Resource(mesh, MapResourceKind.Mesh), Resource(collider, MapResourceKind.Collider))
        }.ToJsonString());
        var document = new MapDocument
        {
            Id = "rotation-cancellation-world",
            TileSize = 64,
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = new() { root },
            Bounds = new() { MinX = 926784, MinZ = 0, MaxX = 926848, MaxZ = 64 },
            PlayableBounds = new() { MinX = 926784, MinZ = 0, MaxX = 926848, MaxZ = 64 }
        };
        document.Placements.Add(new()
        {
            Id = "rotation-cancellation",
            Kind = "prop",
            AssetId = "rotation-cancellation",
            X = 0,
            Y = 0,
            Z = 63.9375f,
            Yaw = (float)(Math.PI / 4),
            Scale = 1
        });
        if (preciseYaw)
            document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Placement,
                "rotation-cancellation", new(0, 0, 63.9375, Math.PI / 4, 1), new string('a', 64)) };
        return MapWorldBuilder.Build(document, MapAssetClosure.Load(new[] { root }, source), Options());
    }

    internal static (Vector3 Centre, Vector3 WideCentre, Vector3 Min, Vector3 Max) InstalledBox(BepuPhysicsWorld physics)
    {
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        // Owned-test registry inspection, matching InstalledPoseFixture. All geometry reads use public Bepu APIs.
        var field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        var simulation = Assert.IsType<BepuPhysics.Simulation>(field!.GetValue(physics));
        Assert.Equal(1, simulation.Statics.Count);
        ref var body = ref simulation.Statics[0];
        ref var compound = ref simulation.Shapes.GetShape<BepuPhysics.Collidables.Compound>(body.Shape.Index);
        Assert.Equal(1, compound.Children.Length);
        ref var child = ref compound.Children[0];
        BepuPhysics.Collidables.Compound.GetWorldPose(child.LocalPose, body.Pose, out var pose);
        BepuUtilities.QuaternionWide.Broadcast(body.Pose.Orientation, out var wideOrientation);
        BepuUtilities.Vector3Wide.Broadcast(child.LocalPose.Position, out var wideOffset);
        BepuUtilities.QuaternionWide.TransformWithoutOverlap(wideOffset, wideOrientation, out var rotated);
        BepuUtilities.Vector3Wide.Broadcast(body.Pose.Position, out var translation);
        BepuUtilities.Vector3Wide.Add(rotated, translation, out var widePosition);
        var wideCentre = new Vector3(widePosition.X[0], widePosition.Y[0], widePosition.Z[0]);
        ref var box = ref simulation.Shapes.GetShape<BepuPhysics.Collidables.Box>(child.ShapeIndex.Index);
        box.ComputeBounds(pose.Orientation, out var min, out var max);
        return (pose.Position, wideCentre, pose.Position + min, pose.Position + max);
    }
}
