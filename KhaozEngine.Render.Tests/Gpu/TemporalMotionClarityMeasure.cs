using System;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Display-luma gradient energy, matched-pose error, added change and sustained stop recovery.</summary>
internal sealed record TemporalMotionClarityMeasure
{
    const int W = TemporalMotionClarityStage.W, H = TemporalMotionClarityStage.H;
    internal static readonly PixelRect DetailRegion = new(16, 16, 224, 60);
    internal static readonly PixelRect TextureRegion = new(16, 88, 224, 128);
    internal sealed record Region(double Energy, double ReferenceEnergy, double Sharpness, double Rms,
        double AddedChange, double RemovedChange);
    internal sealed record Frame(int Index, string Phase, Region Detail, Region Control,
        TemporalMotionClarityProbe.Diagnostics? Diagnostics);

    public double Sharpness { get; init; }
    public double Energy { get; init; }
    public double Rms { get; init; }
    public double AddedChange { get; init; }
    public double RemovedChange { get; init; }
    public int WorstPair { get; init; }
    public double WorstPairAddedChange { get; init; }
    public int? SustainedRecoveryFrames { get; init; }
    public double SettledSharpness { get; init; }
    public double PlateauChange { get; init; }

    internal static Region Measure(byte[] image, byte[] reference, PixelRect rect,
        byte[]? previous = null, byte[]? previousReference = null)
    {
        double energy = 0, referenceEnergy = 0, error = 0;
        for (int y = rect.Y0; y < rect.Y1; y++)
            for (int x = rect.X0; x < rect.X1; x++)
            {
                int p = y * W + x;
                double v = DaylightShadowMeasure.Luma(image, p), r = DaylightShadowMeasure.Luma(reference, p);
                double dx = DaylightShadowMeasure.Luma(image, p + 1) - v;
                double dy = DaylightShadowMeasure.Luma(image, p + W) - v;
                double rx = DaylightShadowMeasure.Luma(reference, p + 1) - r;
                double ry = DaylightShadowMeasure.Luma(reference, p + W) - r;
                energy += dx * dx + dy * dy;
                referenceEnergy += rx * rx + ry * ry;
                error += (v - r) * (v - r);
            }
        double added = 0, removed = 0;
        if (previous is not null && previousReference is not null)
        {
            byte[][] pair = [previous, image], referencePair = [previousReference, reference];
            added = TemporalAcceptance.AddedChange(pair, referencePair, W, H, rect);
            removed = TemporalAcceptance.RemovedChange(pair, referencePair, W, H, rect);
        }
        return new Region(energy / rect.Area, referenceEnergy / rect.Area, energy / referenceEnergy,
            Math.Sqrt(error / rect.Area), added, removed);
    }

    internal static TemporalMotionClarityMeasure Summarize(Frame[] frames, bool detail)
    {
        Region RegionOf(Frame frame) => detail ? frame.Detail : frame.Control;
        Frame[] moving = frames.Skip(64).Take(32).ToArray();
        double settled = frames.TakeLast(32).Average(f => RegionOf(f).Sharpness);
        Frame worst = moving.MaxBy(f => RegionOf(f).AddedChange)!;
        int lastOutside = -1;
        for (int i = TemporalMotionClarityStage.Moving; i < frames.Length; i++)
            if (!(Math.Abs(RegionOf(frames[i]).Sharpness / settled - 1) <= 0.05))
                lastOutside = i - TemporalMotionClarityStage.Moving;
        double previous16 = frames.Skip(frames.Length - 32).Take(16).Average(f => RegionOf(f).Energy);
        double last16 = frames.TakeLast(16).Average(f => RegionOf(f).Energy);
        return new TemporalMotionClarityMeasure
        {
            Sharpness = moving.Average(f => RegionOf(f).Sharpness),
            Energy = moving.Average(f => RegionOf(f).Energy),
            Rms = moving.Average(f => RegionOf(f).Rms),
            AddedChange = moving.Average(f => RegionOf(f).AddedChange),
            RemovedChange = moving.Average(f => RegionOf(f).RemovedChange),
            WorstPair = worst.Index,
            WorstPairAddedChange = RegionOf(worst).AddedChange,
            SustainedRecoveryFrames = lastOutside == TemporalMotionClarityStage.Stopped - 1
                ? null : lastOutside + 2,
            SettledSharpness = settled,
            PlateauChange = Math.Abs(last16 / previous16 - 1),
        };
    }
}
