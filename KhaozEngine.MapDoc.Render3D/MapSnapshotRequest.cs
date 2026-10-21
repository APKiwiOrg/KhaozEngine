using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public enum MapCaptureRequirement { CompleteScope, Preview }
public enum MapCaptureAa { Native, Quality, Balanced }
public enum MapCaptureRoute { View, TopDown }
public enum MapCaptureFocusSource { Unresolved, Explicit, ViewEye, TopDownSupport }
public sealed record MapSnapshotFraming(MapFramePoint Eye, MapFramePoint Target, bool Orthographic,
    float VerticalSizeOrFovDegrees, int Width, int Height, float NearPlane = .1f, float FarPlane = 64f)
{
    public Matrix4x4 ViewProjection => Camera.ViewProjection;
    internal IIsoCamera3D Camera => new CaptureCamera(this);
    sealed class CaptureCamera(MapSnapshotFraming framing) : IIsoCamera3D, IRenderOriginAware
    {
        public Vector3 Eye => Relative(framing.Eye, Vector3.Zero);
        public Vector3 RenderOrigin { get; set; }
        public Vector3 Forward => Vector3.Normalize(new((float)((framing.Target.Frame.X - framing.Eye.Frame.X) * 128d + framing.Target.Local.X - framing.Eye.Local.X),
            framing.Target.Local.Y - framing.Eye.Local.Y, (float)((framing.Target.Frame.Z - framing.Eye.Frame.Z) * 128d + framing.Target.Local.Z - framing.Eye.Local.Z)));
        Matrix4x4 ViewAt(Vector3 origin)
        {
            var eye = Relative(framing.Eye, origin);
            return Matrix4x4.CreateLookAt(eye, eye + Forward, MathF.Abs(Vector3.Dot(Forward, Vector3.UnitY)) > .999f ? Vector3.UnitZ : Vector3.UnitY);
        }
        public Matrix4x4 View => ViewAt(RenderOrigin);
        public Matrix4x4 AbsoluteViewProjection => ViewAt(Vector3.Zero) * Projection;
        public Matrix4x4 Projection => framing.Orthographic
            ? Matrix4x4.CreateOrthographic(framing.VerticalSizeOrFovDegrees * framing.Width / framing.Height,
                framing.VerticalSizeOrFovDegrees, framing.NearPlane, framing.FarPlane)
            : Matrix4x4.CreatePerspectiveFieldOfView(framing.VerticalSizeOrFovDegrees * MathF.PI / 180,
                framing.Width / (float)framing.Height, framing.NearPlane, framing.FarPlane);
        public Matrix4x4 ViewProjection => View * Projection;
        public bool WorldToScreen(Vector3 point, int width, int height, out Vector2 screen)
        {
            var clip = Vector4.Transform(new Vector4(point - RenderOrigin, 1), ViewProjection);
            screen = new((clip.X / clip.W + 1) * width * .5f, (1 - clip.Y / clip.W) * height * .5f);
            return clip.W > 0 && clip.Z >= 0 && clip.Z <= clip.W;
        }
        static Vector3 Relative(MapFramePoint p, Vector3 origin) => new((float)(p.Frame.X * 128d - origin.X + p.Local.X),
            (float)((double)p.Local.Y - origin.Y), (float)(p.Frame.Z * 128d - origin.Z + p.Local.Z));
    }
}
public sealed record MapSnapshotRequest(MapAssetClosure Closure, MapSceneSource Source, MapSceneScope Scope,
    MapSnapshotFraming Framing, MapFramePoint Observer, string? ObserverSpaceId, MapRoofMode RoofMode,
    double TimeSeconds, int WindSeed, string StyleId, MapCapturePolicy Policy, MapCaptureRequirement Requirement,
    MapCaptureRoute Route = MapCaptureRoute.View, MapFramePoint? Focus = null)
{
    public int PreviewWindowLimit { get; init; } = 256;
}
public sealed record MapCaptureDependencyResult(MapSceneStatus Status, MapSceneScope RequestedScope,
    IReadOnlyList<MapSceneScope> RequiredScopes, string LightingDigest);
public sealed record MapCaptureWitness(MapSceneScope RequestedScope, IReadOnlyList<MapSceneScope> RequiredScopes,
    IReadOnlyList<MapSceneScope> AcquiredScopes, MapSceneWitness Scene, bool Capped, string LightingDigest);
public sealed record MapSnapshotResult(MapSceneStatus Status, Render3DCapture? Image, string? Backend,
    MapCaptureWitness Witness, MapRenderReadiness Readiness, MapSnapshotRequest Echo,
    MapCaptureFocusSource FocusSource, IReadOnlyList<string> Findings);
