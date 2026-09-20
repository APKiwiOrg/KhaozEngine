using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>Headless map-editor orbit, pan, dolly, and fly policy over immutable input snapshots.</summary>
internal sealed class EditorNavigationController
{
    const float OrbitSpeed = 0.005f;
    const float InitialOrbitDistance = 25f;
    const float MinDollyDistance = 0.5f;
    const float MaxDollyDistance = 100000f;
    const float DollyStep = 0.85f;

    readonly FlyCamera3D _camera;
    NavigationMode _mode;
    bool _middleRequiresRelease;
    bool _rightRequiresRelease;
    float _flySpeed = 12f;
    float _flySprintMultiplier = 3f;

    /// <summary>True while a middle-button orbit or pan, or a right-button fly gesture is captured.</summary>
    internal bool IsNavigating => _mode != NavigationMode.None;

    /// <summary>The retained navigation pivot. Null until a hit or fallback establishes one.</summary>
    internal Vector3? Pivot { get; private set; }

    /// <summary>World units per second used by right-button fly movement.</summary>
    internal float FlySpeed
    {
        get => _flySpeed;
        set => _flySpeed = PositiveFinite(value, nameof(value));
    }

    /// <summary>Multiplier applied to fly speed while shift is held.</summary>
    internal float FlySprintMultiplier
    {
        get => _flySprintMultiplier;
        set => _flySprintMultiplier = PositiveFinite(value, nameof(value));
    }

    internal EditorNavigationController(FlyCamera3D camera) =>
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));

    /// <summary>Updates navigation once for the supplied frame.</summary>
    internal void Update(in InputState input, bool viewportEligible, Vector3? terrainHit, float dt)
    {
        EnsureFiniteCamera();
        ObserveReleases(input);

        if (!input.WindowFocused)
        {
            Cancel();
            return;
        }

        EndReleasedGesture(input);
        if (_mode == NavigationMode.None && viewportEligible)
            TryAcquire(input, terrainHit);

        if (input.IsCommandDown)
        {
            EnsureFiniteCamera();
            return;
        }

        if (viewportEligible && float.IsFinite(input.ScrollDelta) && input.ScrollDelta != 0f)
            Dolly(input.ScrollDelta, _mode == NavigationMode.None ? terrainHit : null);

        Vector2 delta = IsFinite(input.MouseDelta) ? input.MouseDelta : Vector2.Zero;
        switch (_mode)
        {
            case NavigationMode.Orbit when input.IsDown(MouseButton.Middle):
                Orbit(delta);
                break;
            case NavigationMode.Pan when input.IsDown(MouseButton.Middle):
                Pan(delta, input.Height);
                break;
            case NavigationMode.Fly when input.IsDown(MouseButton.Right):
                Fly(input, delta, dt);
                break;
        }

        EnsureFiniteCamera();
    }

    /// <summary>Cancels capture and refuses the captured button until it is released.</summary>
    internal void Cancel()
    {
        _middleRequiresRelease |= _mode is NavigationMode.Orbit or NavigationMode.Pan;
        _rightRequiresRelease |= _mode == NavigationMode.Fly;
        _mode = NavigationMode.None;
    }

    /// <summary>Sets a finite pivot and places the camera at the requested distance along its current view.</summary>
    internal void Frame(Vector3 pivot, float distance)
    {
        if (!IsFinite(pivot)) throw new ArgumentOutOfRangeException(nameof(pivot));
        if (!float.IsFinite(distance) || distance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(distance));
        Cancel();
        Pivot = pivot;
        _camera.Position = pivot - _camera.Forward * Math.Clamp(distance, MinDollyDistance, MaxDollyDistance);
        EnsureFiniteCamera();
    }

    void TryAcquire(in InputState input, Vector3? terrainHit)
    {
        if (input.WasPressed(MouseButton.Middle) && !_middleRequiresRelease)
        {
            EstablishPivot(terrainHit);
            bool shift = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);
            _mode = shift ? NavigationMode.Pan : NavigationMode.Orbit;
        }
        else if (input.WasPressed(MouseButton.Right) && !_rightRequiresRelease)
        {
            EstablishPivot(terrainHit);
            _mode = NavigationMode.Fly;
        }
    }

    void EndReleasedGesture(in InputState input)
    {
        if (_mode is NavigationMode.Orbit or NavigationMode.Pan)
        {
            if (input.WasReleased(MouseButton.Middle) || !input.IsDown(MouseButton.Middle))
                _mode = NavigationMode.None;
        }
        else if (_mode == NavigationMode.Fly
            && (input.WasReleased(MouseButton.Right) || !input.IsDown(MouseButton.Right)))
        {
            _mode = NavigationMode.None;
        }
    }

    void ObserveReleases(in InputState input)
    {
        if (!input.IsDown(MouseButton.Middle)) _middleRequiresRelease = false;
        if (!input.IsDown(MouseButton.Right)) _rightRequiresRelease = false;
    }

    void EstablishPivot(Vector3? terrainHit)
    {
        if (terrainHit is Vector3 hit && IsFinite(hit))
        {
            Pivot = hit;
            return;
        }
        if (Pivot is Vector3 retained && IsFinite(retained)) return;
        Pivot = _camera.Position + _camera.Forward * InitialOrbitDistance;
    }

    void Orbit(Vector2 delta)
    {
        if (delta == Vector2.Zero || Pivot is not Vector3 pivot) return;
        float distance = ValidDistance(Vector3.Distance(_camera.Position, pivot), InitialOrbitDistance);
        _camera.Yaw -= delta.X * OrbitSpeed;
        _camera.Pitch -= delta.Y * OrbitSpeed;
        _camera.Position = pivot - _camera.Forward * distance;
    }

    void Pan(Vector2 delta, int viewportHeight)
    {
        if (delta == Vector2.Zero || Pivot is not Vector3 pivot) return;
        float distance = ValidDistance(Vector3.Distance(_camera.Position, pivot), InitialOrbitDistance);
        viewportHeight = Math.Max(1, viewportHeight);
        float fov = float.IsFinite(_camera.FieldOfView) && _camera.FieldOfView > 0f
            ? _camera.FieldOfView
            : MathF.PI / 3f;
        float worldPerPixel = 2f * distance * MathF.Tan(fov * 0.5f) / viewportHeight;
        Vector3 forward = _camera.Forward;
        Vector3 right = SafeNormalize(Vector3.Cross(forward, Vector3.UnitY), -Vector3.UnitX);
        Vector3 up = SafeNormalize(Vector3.Cross(right, forward), Vector3.UnitY);
        Vector3 translation = right * (-delta.X * worldPerPixel) + up * (delta.Y * worldPerPixel);
        if (!IsFinite(translation)) return;
        _camera.Position += translation;
        Pivot = pivot + translation;
    }

    void Dolly(float scroll, Vector3? terrainHit)
    {
        EstablishPivot(terrainHit);
        if (Pivot is not Vector3 pivot) return;
        Vector3 offset = _camera.Position - pivot;
        float distance = ValidDistance(offset.Length(), InitialOrbitDistance);
        Vector3 direction = SafeNormalize(offset, -_camera.Forward);
        float factor = MathF.Pow(DollyStep, scroll);
        float nextDistance = float.IsFinite(factor)
            ? Math.Clamp(distance * factor, MinDollyDistance, MaxDollyDistance)
            : scroll > 0f ? MinDollyDistance : MaxDollyDistance;
        _camera.Position = pivot + direction * nextDistance;
    }

    void Fly(in InputState input, Vector2 delta, float dt)
    {
        _camera.Yaw -= delta.X * OrbitSpeed;
        _camera.Pitch -= delta.Y * OrbitSpeed;
        if (!float.IsFinite(dt) || dt <= 0f) return;

        Vector3 forward = _camera.Forward;
        Vector3 right = SafeNormalize(Vector3.Cross(forward, Vector3.UnitY), -Vector3.UnitX);
        Vector3 move = Vector3.Zero;
        if (input.IsDown(Key.W)) move += forward;
        if (input.IsDown(Key.S)) move -= forward;
        if (input.IsDown(Key.D)) move += right;
        if (input.IsDown(Key.A)) move -= right;
        if (input.IsDown(Key.E)) move += Vector3.UnitY;
        if (input.IsDown(Key.Q)) move -= Vector3.UnitY;
        if (move == Vector3.Zero) return;

        float speed = FlySpeed;
        if (input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift)) speed *= FlySprintMultiplier;
        _camera.Position += SafeNormalize(move, Vector3.Zero) * (speed * dt);
    }

    void EnsureFiniteCamera()
    {
        if (!IsFinite(_camera.Position)) _camera.Position = Vector3.Zero;
        if (!float.IsFinite(_camera.Yaw)) _camera.Yaw = 0f;
        if (!float.IsFinite(_camera.Pitch)) _camera.Pitch = 0f;
        if (Pivot is Vector3 pivot && !IsFinite(pivot)) Pivot = null;
    }

    static float ValidDistance(float value, float fallback) =>
        float.IsFinite(value) && value > 0.0001f ? value : fallback;

    static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-12f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    static float PositiveFinite(float value, string paramName) =>
        float.IsFinite(value) && value > 0f
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, "Value must be finite and greater than zero.");

    enum NavigationMode { None, Orbit, Pan, Fly }
}
