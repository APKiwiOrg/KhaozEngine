using System;
using System.Text.Json.Serialization;

namespace KhaozEngine.Tests.Attachments;

[JsonConverter(typeof(JsonStringEnumConverter<FlatLipBearingVerdict>))]
internal enum FlatLipBearingVerdict
{
    Ineligible,
    QualifiedUnload,
    Bearing,
    Closing,
    Recovered,
    StrictGap,
    NotClosing,
    Late,
}

internal readonly record struct FlatLipBearingDecision(FlatLipBearingVerdict Verdict, double? EntrySeconds = null)
{
    internal bool Acceptable => Verdict is not
        (FlatLipBearingVerdict.StrictGap or FlatLipBearingVerdict.NotClosing or FlatLipBearingVerdict.Late);
}

// The geometry oracle supplies the entry clock independently of the lining gap.
internal sealed class FlatLipReentry(Func<double, bool> eligibleAt)
{
    private const double RecoveryWindowSeconds = .025;
    private bool _established;
    private bool _qualifiedUnload;
    private double? _entry;
    private double _previousSeconds;
    private float _previousGap;

    internal FlatLipBearingDecision Observe(double seconds, bool eligible, float gap, float lipClearance)
    {
        FlatLipBearingDecision result = Evaluate(seconds, eligible, gap, lipClearance);
        _previousSeconds = seconds;
        _previousGap = gap;
        return result;
    }

    private FlatLipBearingDecision Evaluate(double seconds, bool eligible, float gap, float lipClearance)
    {
        if (_entry is double entry)
            return EvaluateWindow(seconds, eligible, gap, entry);

        if (!eligible)
        {
            if (_established && gap > HangingPanelFixture.BearingLimit &&
                lipClearance > HangingPanelFixture.ClearanceTolerance)
            {
                _qualifiedUnload = true;
                return new(FlatLipBearingVerdict.QualifiedUnload);
            }
            return new(FlatLipBearingVerdict.Ineligible);
        }

        if (_qualifiedUnload)
        {
            _qualifiedUnload = false;
            entry = FindEntry(_previousSeconds, seconds);
            _entry = entry;
            return EvaluateWindow(seconds, eligible, gap, entry);
        }

        if (!(gap <= HangingPanelFixture.BearingLimit))
            return new(FlatLipBearingVerdict.StrictGap);

        _established = true;
        return new(FlatLipBearingVerdict.Bearing);
    }

    private FlatLipBearingDecision EvaluateWindow(double seconds, bool eligible, float gap, double entry)
    {
        if (seconds > entry + RecoveryWindowSeconds)
        {
            _entry = null;
            return new(FlatLipBearingVerdict.Late, entry);
        }

        if (eligible && gap <= HangingPanelFixture.BearingLimit)
        {
            _entry = null;
            return new(FlatLipBearingVerdict.Recovered, entry);
        }

        if (!(gap <= HangingPanelFixture.BearingLimit || gap < _previousGap))
        {
            _entry = null;
            return new(FlatLipBearingVerdict.NotClosing, entry);
        }
        return new(FlatLipBearingVerdict.Closing, entry);
    }

    private double FindEntry(double ineligible, double eligible)
    {
        if (eligibleAt(ineligible) || !eligibleAt(eligible))
            throw new InvalidOperationException("Consecutive fixture ticks must bracket a geometric entry.");

        // The continuous fixture motion has one crossing between these adjacent ticks.
        for (int iteration = 0; iteration < 64; iteration++)
        {
            double middle = ineligible + (eligible - ineligible) / 2;
            if (middle == ineligible || middle == eligible) break;
            if (eligibleAt(middle))
                eligible = middle;
            else
                ineligible = middle;
        }
        return eligible;
    }
}
