using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Request collection and ordering for point-shadow residency.</summary>
public sealed partial class Scene3D
{
    internal bool PreparePointShadowRequests(Vector3 eyeAbsolute) =>
        GatherPointShadowRequests(Post.Quality.Shadows.PointShadows, eyeAbsolute);

    internal bool PointRequestCanRetainSkinned(
        in PointShadowCasterSphere caster, PointShadowSettings settings)
    {
        bool staticAtlasLive = _pointShadowAtlas is not null;
        int dynamicRequestsRemaining = Math.Max(0, settings.MaxDynamicLightsPerFrame);
        for (int i = 0; i < _pointRequests.Count; i++)
        {
            PointShadowRequest request = _pointRequests[i];
            if (request.Mode == LightShadowMode.Static)
            {
                if (!staticAtlasLive) continue;
            }
            else if (dynamicRequestsRemaining-- <= 0)
            {
                continue;
            }

            if (caster.TouchesShadowingShell(request.Position, request.Radius, request.NearRadius,
                request.ExclusionMin, request.ExclusionMax))
                return true;
        }
        return false;
    }

    /// <summary>Collect every submitted request. Keyed statics sort first by stable key, independent of the eye,
    /// and dynamic effects use their traditional nearest-first order after them.</summary>
    bool GatherPointShadowRequests(PointShadowSettings settings, Vector3 eyeAbsolute)
    {
        _pointRequests.Clear();
        _pointShadowStaticRequests = 0;
        if (!settings.Enabled) return false;

        for (int i = 0; i < _lights.Count; i++)
        {
            LightShadow request = _lights[i].Shadow;
            if (!request.Requested) continue;
            Vector4 posRadius = _lights[i].PosRadius;
            var position = new Vector3(posRadius.X, posRadius.Y, posRadius.Z);
            long key = request.Mode == LightShadowMode.Dynamic ? i : request.Key;
            Vector3 exclusionMin = request.HasExclusionBox ? request.ExclusionMin : Vector3.Zero;
            Vector3 exclusionMax = request.HasExclusionBox ? request.ExclusionMax : Vector3.Zero;
            _pointRequests.Add(new PointShadowRequest(
                i, position, posRadius.W, request.NearRadius, exclusionMin, exclusionMax, request.Mode, key,
                (position - eyeAbsolute).LengthSquared()));
            if (request.Mode == LightShadowMode.Static) _pointShadowStaticRequests++;
        }
        if (_pointRequests.Count == 0) return false;

        _pointRequests.Sort(static (a, b) =>
        {
            bool aStatic = a.Mode == LightShadowMode.Static;
            bool bStatic = b.Mode == LightShadowMode.Static;
            if (aStatic != bStatic) return aStatic ? -1 : 1;
            if (aStatic)
            {
                int byKey = a.Key.CompareTo(b.Key);
                return byKey != 0 ? byKey : a.LightIndex.CompareTo(b.LightIndex);
            }
            int byDistance = a.DistanceSq.CompareTo(b.DistanceSq);
            return byDistance != 0 ? byDistance : a.LightIndex.CompareTo(b.LightIndex);
        });
        return true;
    }

    void EnsurePointSlotUniformCapacity(int required)
    {
        if (_pointSlotUniform.Length >= required) return;
        int capacity = Math.Max(16, _pointSlotUniform.Length);
        while (capacity < required)
            capacity = capacity > int.MaxValue / 2 ? required : capacity * 2;
        Array.Resize(ref _pointSlotUniform, capacity);
    }
}
