namespace KhaozEngine.Windowing;

/// <summary>Preserve one native contact admission through its subsequent momentum phase.</summary>
internal sealed class NativePanZoomAdmission
{
    bool _hasSequence, _admitted, _ended;

    internal bool Accept(PointerGestureSample sample, bool admitsStart)
    {
        if (sample.IsMomentum)
        {
            bool accepted = _hasSequence && _admitted;
            if (sample.Phase == PointerGestureInputPhase.Ended) Reset();
            return accepted;
        }
        if (sample.Phase == PointerGestureInputPhase.None) return admitsStart;
        if (sample.Phase == PointerGestureInputPhase.Began)
        {
            _hasSequence = true;
            _admitted = admitsStart;
            _ended = false;
        }
        bool result = _hasSequence && !_ended && _admitted;
        if (sample.Phase == PointerGestureInputPhase.Ended) _ended = true;
        return result;
    }

    internal void Reset() => _hasSequence = _admitted = _ended = false;
}
