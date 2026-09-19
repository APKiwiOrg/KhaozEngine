using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Debug;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase;

public sealed partial class Room3D
{
    Room3DEnvironment? _environment;
    IReadOnlyList<Room3DStep> _stairs = Array.Empty<Room3DStep>();
    IReadOnlyList<PropPlacement> _cascadeTrees = Array.Empty<PropPlacement>();

    void BuildTestbedFixtures()
    {
        _stairs = Room3DTestbed.CreateSteps(_terrain.GroundHeight);
        foreach (Room3DStep step in _stairs)
        {
            var shape = new BoxShape(step.HalfExtents);
            var pose = Pose.At(step.Center);
            _physics.AddStatic(shape, pose);
            _overlayStatics.Add(new CollisionStatic(shape, pose));
        }

        _cascadeTrees = Room3DTestbed.CreateTreeLine(_camera.Eye, _camera.Target,
            _scene.Post.Quality.Shadows.ShadowNearDistance, _terrain.GroundHeight);
        foreach (PropPlacement tree in _cascadeTrees)
        {
            if (!_collisionShapes.TryGetValue(tree.Id, out PhysicsShape? shape)) continue;
            var pose = Pose.At(new Vector3(tree.X, tree.Y, tree.Z));
            _physics.AddStatic(shape, pose);
            _overlayStatics.Add(new CollisionStatic(shape, pose));
        }
    }

    void UpdateTestbed(float dt)
    {
        if (_environment is null) return;
        if (Manager!.Input.WasPressed(Key.T))
        {
            _environment.Paused = !_environment.Paused;
            _hud.Toast(((LocalizedText)(_environment.Paused
                ? ShowcaseStrings.WorldCyclePaused : ShowcaseStrings.WorldCycleRunning)).Resolve());
        }
        if (Manager.Input.WasPressed(Key.N))
        {
            _environment.ToggleBackground();
            _hud.Toast(((LocalizedText)(_scene.Post.Sky.Enabled
                ? ShowcaseStrings.WorldSky : ShowcaseStrings.WorldStars)).Resolve());
        }
        _environment.Advance(dt);
    }

    void DrawTestbed(Scene3D scene)
    {
        foreach (Room3DStep step in _stairs)
            scene.Draw(_platformMesh, step.World, new Color(0.62f, 0.6f, 0.66f, 1f));
        scene.DrawProps(_cascadeTrees, _propMeshes, _character.Position, PropDrawRadius);
    }

    void DisposeTestbed()
    {
        _environment?.Dispose();
        _environment = null;
        _stairs = Array.Empty<Room3DStep>();
        _cascadeTrees = Array.Empty<PropPlacement>();
    }
}
