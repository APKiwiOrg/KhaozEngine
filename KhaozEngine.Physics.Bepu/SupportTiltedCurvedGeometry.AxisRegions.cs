using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry
{
    bool _axisMode;
    Number _axisCoefficient, _axisOffset;

    GeometrySign AxisParameter(Number coefficient, Number offset, double low, double high, out GeometryInterval parameter)
    {
        parameter = default;
        _axisCoefficient = coefficient; _axisOffset = offset;
        _axisMode = true; _stationaryMode = _boundaryMode = false;
        if (!SolveRoots(low, high, maximumRoots: 1)) return _work.Complete(GeometrySign.Unresolved, ref parameter);
        if (_roots.Count == 0) return _work.Complete(GeometrySign.Negative, ref parameter);
        parameter = _roots.Get(0);
        return _work.Complete(GeometrySign.Positive, ref parameter);
    }

    GeometryInterval AxisValue(GeometryInterval parameter, bool derivative) => _work.Complete(derivative ? B(_axisCoefficient) :
        B(_axisCoefficient).Multiply(parameter).Subtract(B(_axisOffset)));

    GeometrySign AxisSign(double parameter)
    {
        int checkpoint = _arithmetic.Checkpoint;
        GeometrySign sign = Sign(Sub(Mul(_axisCoefficient, C(parameter)), _axisOffset));
        _arithmetic.Restore(checkpoint);
        return _work.Complete(sign);
    }
}
