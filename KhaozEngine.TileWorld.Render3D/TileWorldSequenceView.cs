using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Maintains one snapshot view's camera, observer and resident region ring over a sequence.</summary>
internal sealed class TileWorldSequenceView
{
    readonly Scene3D _scene;
    readonly TileWorldDocument _doc;
    readonly TileWorldView _view;
    readonly FlyCamera3D _camera;
    readonly TileCoord? _observer;
    readonly TileWorldSequenceRegions _regions;
    Vector3 _target;

    internal TileWorldSequenceView(Scene3D scene, TileWorldDocument doc, TileWorldCatalogs catalogs,
        ITileMeshResolver resolver, TileWorldViewOptions options, Vector3 eye, Vector3 target,
        int width, int height, TileCoord? observer)
    {
        _scene = scene;
        _doc = doc;
        _observer = observer;
        _camera = new FlyCamera3D
        {
            FieldOfView = TileWorldSnapshot.PerspectiveFieldOfViewDegrees * MathF.PI / 180f,
            AspectRatio = width / (float)height,
        };
        // Like TileWorldSnapshot's one-shot view, uploaded handles belong to the capture's Scene3D and are
        // destroyed with it. Do not dispose this view after the snapshot call has disposed its scene.
        _view = new TileWorldView(new Scene3DTileWorldScene(scene), doc, catalogs, resolver, options);
        _regions = new TileWorldSequenceRegions(_view, doc);
        ApplyCamera(eye, target);
        UpdateWorld(target);
    }

    internal static void RequireCamera(Vector3 eye, Vector3 target, string parameter)
    {
        float lengthSquared = (target - eye).LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
            throw new ArgumentException("the camera eye and target must be finite and distinct.", parameter);
    }

    internal void Draw(int frame, Func<int, (Vector3 Eye, Vector3 Target)>? cameraFrame)
    {
        if (cameraFrame is not null)
        {
            (Vector3 eye, Vector3 target) = cameraFrame(frame);
            RequireCamera(eye, target, nameof(cameraFrame));
            ApplyCamera(eye, target);
            UpdateWorld(target);
        }
        _view.Draw(_target);
    }

    void ApplyCamera(Vector3 eye, Vector3 target)
    {
        Vector3 look = target - eye;
        _camera.Position = eye;
        _camera.Yaw = MathF.Atan2(look.X, look.Z);
        _camera.Pitch = MathF.Asin(Vector3.Normalize(look).Y);
        _scene.CameraOverride = _camera;
    }

    void UpdateWorld(Vector3 target)
    {
        _target = target;
        var subject = new TileCoord(
            (int)MathF.Floor(TileWorldSpace.TileX(target.X, _doc.TileSize)),
            (int)MathF.Floor(TileWorldSpace.TileZ(target.Z, _doc.TileSize)), 0);
        _view.Observer = _observer ?? subject;
        _regions.Update(subject.Region);
    }
}
