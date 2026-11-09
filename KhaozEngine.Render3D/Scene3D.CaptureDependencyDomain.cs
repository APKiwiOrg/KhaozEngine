using System;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    ShadowCaptureDomain? _captureShadowDomain;
    public void SetCaptureShadowDomain(ShadowCaptureDomain? domain) => _captureShadowDomain = domain;
    int FitCaptureShadowDomain(ShadowCaptureDomain domain)
    {
        int count = Math.Min(domain.Centres.Count, _model.ShadowMap.CascadeCount);
        for (int i = 0; i < count; i++)
        {
            FitCascade(i, -domain.DirectionToLight, ToRender(domain.Centres[i]), domain.Radii[i], _model.ShadowMap.Resolution);
            _cascadeRadii[i] = domain.Radii[i];
        }
        _cascadeCount = count; return count;
    }
    void ConstrainCaptureCasterMasks(int count)
    {
        if (_captureShadowDomain is not { } domain) return;
        for (int i = 0; i < count; i++)
        {
            var sphere = _shadowCasterSpheres[i]; var centre = new System.Numerics.Vector3(sphere.X, sphere.Y, sphere.Z);
            var radius = new System.Numerics.Vector3(sphere.W);
            if (!domain.ContainsCaster(new(centre - radius, centre + radius))) _shadowCasterMasks[i] = 0;
        }
    }
    /// <summary>Finish admitted resource copies before a capture consumes its bounded settle operation.</summary>
    public void CompleteResourceUploads() => _gd.WaitForIdle();
}
