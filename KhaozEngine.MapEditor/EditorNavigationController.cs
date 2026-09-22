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
    const float FlySpeedStep = 1.2f;

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
        => UpdateCore(input, viewportEligible, null, terrainHit, terrainHitResolved: true, dt);

    /// <summary>Updates navigation and resolves terrain only when this frame needs a new pivot.</summary>
    internal void UpdateLazy(in InputState input, bool viewportEligible, Func<Vector3?> terrainHit, float dt)
    {
        ArgumentNullException.ThrowIfNull(terrainHit);
        UpdateCore(input, viewportEligible, terrainHit, null, terrainHitResolved: false, dt);
    }

    void UpdateCore(in InputState input, bool viewportEligible, Func<Vector3?>? terrainHitProvider,
        Vector3? terrainHit, bool terrainHitResolved, float dt)
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
        {
            NavigationMode acquire = AcquisitionMode(input);
            if (acquire != NavigationMode.None)
            {
                if (!terrainHitResolved)
                {
                    terrainHit = terrainHitProvider!();
                    terrainHitResolved = true;
                }
                EstablishPivot(terrainHit);
                _mode = acquire;
            }
        }

        if (input.IsCommandDown)
        {
            EnsureFiniteCamera();
            return;
        }

        // The wheel belongs to the gesture in flight: while right mouse flies it sets the fly speed, the way it did
        // before the wheel took over pivot distance. Everywhere else it dollies.
        if (_mode == NavigationMode.Fly && input.IsDown(MouseButton.Right))
        {
            AdjustFlySpeed(input.ScrollDelta);
        }
        else if (viewportEligible && float.IsFinite(input.ScrollDelta) && input.ScrollDelta != 0f)
        {
            Vector3? dollyHit = null;
            if (_mode == NavigationMode.None)
            {
                if (!terrainHitResolved)
                {
                    terrainHit = terrainHitProvider!();
                    terrainHitResolved = true;
                }
                dollyHit = terrainHit;
            }
            Dolly(input.ScrollDelta, dollyHit);
        }

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

    NavigationMode AcquisitionMode(in InputState input)
    {
        if (input.WasPressed(MouseButton.Middle) && !_middleRequiresRelease)
        {
            bool shift = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);
            return shift ? NavigationMode.Pan : NavigationMode.Orbit;
        }
        return input.WasPressed(MouseButton.Right) && !_rightRequiresRelease
            ? NavigationMode.Fly : NavigationMode.None;
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
        Vector3 offset = _camera.Position - pivot;
        if (!IsFinite(offset) || offset.LengthSquared() <= 1e-12f)
            offset = -_camera.Forward * InitialOrbitDistance;

        float yawDelta = -delta.X * OrbitSpeed;
        if (!float.IsFinite(yawDelta)) yawDelta = 0f;
        float oldPitch = _camera.Pitch;
        float requestedPitchDelta = -delta.Y * OrbitSpeed;
        if (!float.IsFinite(requestedPitchDelta)) requestedPitchDelta = 0f;
        _camera.Yaw += yawDelta;
        _camera.Pitch += requestedPitchDelta;
        float pitchDelta = _camera.Pitch - oldPitch;

        if (yawDelta != 0f)
            offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDelta));
        if (pitchDelta != 0f)
        {
            Vector3 right = SafeNormalize(Vector3.Cross(_camera.Forward, Vector3.UnitY), -Vector3.UnitX);
            offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(right, pitchDelta));
        }
        if (IsFinite(offset)) _camera.Position = pivot + offset;
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

    void AdjustFlySpeed(float scroll)
    {
        if (float.IsNaN(scroll) || scroll == 0f) return;
        float factor = MathF.Pow(FlySpeedStep, scroll);
        float next = float.IsFinite(factor) ? _flySpeed * factor : scroll > 0f ? float.MaxValue : 0f;
        _flySpeed = Math.Clamp(next, EditorSettings.MinFlySpeed, EditorSettings.MaxFlySpeed);
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
