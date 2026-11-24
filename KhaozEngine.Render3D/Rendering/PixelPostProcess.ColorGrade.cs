using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class PixelPostProcess
{
    ColorGradeTonemap? _grade;
    ColorGradeLut? _failedGrade;
    bool _gradeBound;
    internal Exception? ColorGradeFailure { get; private set; }

    float PrepareColorGrade(PixelPostProcessSettings settings, IPostChainTargets targets, GpuRetireQueue? retired)
    {
        ColorGradeLut? look = settings.ColorGrade.Look;
        if (look != _failedGrade || !settings.Hdr.Enabled || settings.ColorGrade.Strength == 0f)
        {
            _failedGrade = null;
            ColorGradeFailure = null;
        }
        if (!settings.Hdr.Enabled || look is null || look.IsIdentity || settings.ColorGrade.Strength == 0f)
        {
            ReleaseColorGrade(retired);
            return 0f;
        }
        if (look == _failedGrade) return 0f;
        if (_grade?.Look != look) ReleaseColorGrade(retired);
        try
        {
            _grade ??= new ColorGradeTonemap(_gd, look, _pingOutput);
            if (!_gradeBound)
            {
                _grade.Bind(_gd, targets, _toneBuf);
                _gradeBound = true;
            }
            return settings.ColorGrade.Strength;
        }
        catch (Exception failure)
        {
            ReleaseColorGrade(retired);
            _failedGrade = look;
            ColorGradeFailure = failure;
            Log.For<PixelPostProcess>().Error($"Could not prepare the colour grade. {failure.Message}");
            return 0f;
        }
    }

    void ReleaseColorGrade(GpuRetireQueue? retired)
    {
        ResourceRetirement.Free(_grade, retired);
        _grade = null;
        _gradeBound = false;
    }

    void ReleaseColorGradeSets(GpuRetireQueue? retired)
    {
        _grade?.ReleaseSets(retired);
        _gradeBound = false;
    }
}
