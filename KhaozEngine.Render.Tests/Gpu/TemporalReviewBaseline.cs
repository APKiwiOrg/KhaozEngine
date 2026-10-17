using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    internal readonly record struct TemporalReviewFigures(double Error, double Change, double Temporal, double Reference)
    {
        public override string ToString() => $"{Error:0.000000}|{Change:0.000000}|{Temporal:0.000000}|{Reference:0.000000}";
    }

    /// <summary>v21.2.0 measurements per device class. Error and temporal error stay within a tenth. Diagonal lines
    /// also bound raw change. Moving rims report raw change against the reference, since better tracking can raise it
    /// legitimately. The half unit in the last printed place covers rounding of the stored six-decimal figures.</summary>
    internal static class TemporalReviewBaseline
    {
        const double Tolerance = 0.1, Rounding = 0.0000005;
        static readonly Dictionary<(string Device, string Kind, string Name), TemporalReviewFigures> Before = new()
        {
            [("hardware", "diagonal", "Native 30deg 10h pan0.0")] = new(0.004231, 0.000965, 0.000965, 0.000000),
            [("hardware", "diagonal", "Native 30deg 10h pan0.3")] = new(0.009083, 0.002944, 0.004849, 0.007189),
            [("hardware", "diagonal", "Native 30deg 2h pan0.0")] = new(0.001098, 0.000193, 0.000193, 0.000000),
            [("hardware", "diagonal", "Native 30deg 2h pan0.3")] = new(0.002823, 0.001082, 0.001217, 0.001929),
            [("hardware", "diagonal", "Native 30deg 5h pan0.0")] = new(0.002095, 0.000483, 0.000483, 0.000000),
            [("hardware", "diagonal", "Native 30deg 5h pan0.3")] = new(0.004928, 0.001647, 0.002487, 0.003716),
            [("hardware", "diagonal", "Native 45deg 10h pan0.0")] = new(0.003046, 0.001032, 0.001032, 0.000000),
            [("hardware", "diagonal", "Native 45deg 10h pan0.3")] = new(0.011356, 0.002524, 0.008611, 0.010600),
            [("hardware", "diagonal", "Native 45deg 2h pan0.0")] = new(0.001123, 0.000219, 0.000219, 0.000000),
            [("hardware", "diagonal", "Native 45deg 2h pan0.3")] = new(0.003272, 0.001057, 0.001943, 0.002513),
            [("hardware", "diagonal", "Native 45deg 5h pan0.0")] = new(0.001656, 0.000519, 0.000519, 0.000000),
            [("hardware", "diagonal", "Native 45deg 5h pan0.3")] = new(0.006099, 0.001463, 0.004393, 0.005409),
            [("hardware", "diagonal", "Quality 30deg 10h pan0.0")] = new(0.007768, 0.001385, 0.001385, 0.000000),
            [("hardware", "diagonal", "Quality 30deg 10h pan0.3")] = new(0.018769, 0.004048, 0.009143, 0.012444),
            [("hardware", "diagonal", "Quality 30deg 2h pan0.0")] = new(0.002100, 0.000292, 0.000292, 0.000000),
            [("hardware", "diagonal", "Quality 30deg 2h pan0.3")] = new(0.004780, 0.001100, 0.001985, 0.002704),
            [("hardware", "diagonal", "Quality 30deg 5h pan0.0")] = new(0.004084, 0.000696, 0.000696, 0.000000),
            [("hardware", "diagonal", "Quality 30deg 5h pan0.3")] = new(0.009811, 0.002108, 0.004616, 0.006273),
            [("hardware", "diagonal", "Quality 45deg 10h pan0.0")] = new(0.009786, 0.002277, 0.002277, 0.000000),
            [("hardware", "diagonal", "Quality 45deg 10h pan0.3")] = new(0.022962, 0.003422, 0.014944, 0.017454),
            [("hardware", "diagonal", "Quality 45deg 2h pan0.0")] = new(0.002313, 0.000300, 0.000300, 0.000000),
            [("hardware", "diagonal", "Quality 45deg 2h pan0.3")] = new(0.005485, 0.001024, 0.003127, 0.003729),
            [("hardware", "diagonal", "Quality 45deg 5h pan0.0")] = new(0.004355, 0.000801, 0.000801, 0.000000),
            [("hardware", "diagonal", "Quality 45deg 5h pan0.3")] = new(0.011530, 0.001805, 0.007445, 0.008811),
            [("hardware", "rim", "Native M 0.5 leading flat")] = new(0.010093, 0.005837, 0.005837, 0.000000),
            [("hardware", "rim", "Native M 0.5 leading textured")] = new(0.012557, 0.011524, 0.009809, 0.018509),
            [("hardware", "rim", "Native M 0.5 trailing flat")] = new(0.000904, 0.000991, 0.000991, 0.000000),
            [("hardware", "rim", "Native M 0.5 trailing textured")] = new(0.020278, 0.015492, 0.013524, 0.018943),
            [("hardware", "rim", "Native M 1.0 leading flat")] = new(0.006974, 0.007067, 0.007067, 0.000000),
            [("hardware", "rim", "Native M 1.0 leading textured")] = new(0.003683, 0.033568, 0.003982, 0.031816),
            [("hardware", "rim", "Native M 1.0 trailing flat")] = new(0.000057, 0.000057, 0.000057, 0.000000),
            [("hardware", "rim", "Native M 1.0 trailing textured")] = new(0.008988, 0.041984, 0.009774, 0.035586),
            [("hardware", "rim", "Native M 2.0 leading flat")] = new(0.010102, 0.015220, 0.008355, 0.006865),
            [("hardware", "rim", "Native M 2.0 leading textured")] = new(0.013744, 0.056622, 0.018523, 0.062212),
            [("hardware", "rim", "Native M 2.0 trailing flat")] = new(0.000057, 0.000057, 0.000057, 0.000000),
            [("hardware", "rim", "Native M 2.0 trailing textured")] = new(0.012455, 0.062360, 0.012455, 0.056131),
            [("hardware", "rim", "Native M 4.0 leading flat")] = new(0.005335, 0.037241, 0.005350, 0.031935),
            [("hardware", "rim", "Native M 4.0 leading textured")] = new(0.008627, 0.060767, 0.010109, 0.064929),
            [("hardware", "rim", "Native M 4.0 trailing flat")] = new(0.000035, 0.000032, 0.000032, 0.000000),
            [("hardware", "rim", "Native M 4.0 trailing textured")] = new(0.008266, 0.061401, 0.010205, 0.066148),
            [("hardware", "rim", "Native M 8.0 leading flat")] = new(0.004401, 0.092820, 0.004689, 0.088200),
            [("hardware", "rim", "Native M 8.0 leading textured")] = new(0.010910, 0.063644, 0.010996, 0.066294),
            [("hardware", "rim", "Native M 8.0 trailing flat")] = new(0.000077, 0.000057, 0.000057, 0.000000),
            [("hardware", "rim", "Native M 8.0 trailing textured")] = new(0.008359, 0.060477, 0.010364, 0.063109),
            [("hardware", "rim", "Native MS 1.0 leading flat")] = new(0.005423, 0.010341, 0.008185, 0.008146),
            [("hardware", "rim", "Native MS 1.0 leading textured")] = new(0.004220, 0.033898, 0.005468, 0.031803),
            [("hardware", "rim", "Native MS 1.0 trailing flat")] = new(0.000140, 0.000128, 0.000128, 0.000000),
            [("hardware", "rim", "Native MS 1.0 trailing textured")] = new(0.009638, 0.044340, 0.012826, 0.038593),
            [("hardware", "rim", "Native MS 2.0 leading flat")] = new(0.009518, 0.020195, 0.010665, 0.013942),
            [("hardware", "rim", "Native MS 2.0 leading textured")] = new(0.022944, 0.056261, 0.029795, 0.062223),
            [("hardware", "rim", "Native MS 2.0 trailing flat")] = new(0.000140, 0.000128, 0.000128, 0.000000),
            [("hardware", "rim", "Native MS 2.0 trailing textured")] = new(0.014849, 0.064188, 0.016698, 0.058579),
            [("hardware", "rim", "Quality M 0.5 leading flat")] = new(0.009307, 0.006161, 0.006161, 0.000000),
            [("hardware", "rim", "Quality M 0.5 leading textured")] = new(0.015236, 0.009853, 0.010976, 0.018509),
            [("hardware", "rim", "Quality M 0.5 trailing flat")] = new(0.007357, 0.005763, 0.005763, 0.000000),
            [("hardware", "rim", "Quality M 0.5 trailing textured")] = new(0.027410, 0.016152, 0.016248, 0.018943),
            [("hardware", "rim", "Quality M 1.0 leading flat")] = new(0.008298, 0.007916, 0.007916, 0.000000),
            [("hardware", "rim", "Quality M 1.0 leading textured")] = new(0.003164, 0.032183, 0.004926, 0.031816),
            [("hardware", "rim", "Quality M 1.0 trailing flat")] = new(0.000223, 0.000223, 0.000223, 0.000000),
            [("hardware", "rim", "Quality M 1.0 trailing textured")] = new(0.009706, 0.038906, 0.008060, 0.035586),
            [("hardware", "rim", "Quality M 2.0 leading flat")] = new(0.010920, 0.014960, 0.008095, 0.006865),
            [("hardware", "rim", "Quality M 2.0 leading textured")] = new(0.043105, 0.055008, 0.048357, 0.062212),
            [("hardware", "rim", "Quality M 2.0 trailing flat")] = new(0.000969, 0.000974, 0.000974, 0.000000),
            [("hardware", "rim", "Quality M 2.0 trailing textured")] = new(0.020791, 0.055604, 0.017645, 0.056131),
            [("hardware", "rim", "Quality M 4.0 leading flat")] = new(0.007250, 0.048955, 0.017083, 0.031935),
            [("hardware", "rim", "Quality M 4.0 leading textured")] = new(0.021481, 0.052685, 0.027760, 0.064929),
            [("hardware", "rim", "Quality M 4.0 trailing flat")] = new(0.000272, 0.000295, 0.000295, 0.000000),
            [("hardware", "rim", "Quality M 4.0 trailing textured")] = new(0.017544, 0.050289, 0.024648, 0.066148),
            [("hardware", "rim", "Quality M 8.0 leading flat")] = new(0.004424, 0.092742, 0.004684, 0.088200),
            [("hardware", "rim", "Quality M 8.0 leading textured")] = new(0.022060, 0.059542, 0.021873, 0.066294),
            [("hardware", "rim", "Quality M 8.0 trailing flat")] = new(0.000966, 0.000970, 0.000970, 0.000000),
            [("hardware", "rim", "Quality M 8.0 trailing textured")] = new(0.016565, 0.051855, 0.023971, 0.063109),
            [("hardware", "rim", "Quality MS 1.0 leading flat")] = new(0.007375, 0.010636, 0.010598, 0.008146),
            [("hardware", "rim", "Quality MS 1.0 leading textured")] = new(0.004789, 0.034655, 0.008459, 0.031803),
            [("hardware", "rim", "Quality MS 1.0 trailing flat")] = new(0.000292, 0.000382, 0.000382, 0.000000),
            [("hardware", "rim", "Quality MS 1.0 trailing textured")] = new(0.010703, 0.041697, 0.012784, 0.038593),
            [("hardware", "rim", "Quality MS 2.0 leading flat")] = new(0.009152, 0.018185, 0.009405, 0.013942),
            [("hardware", "rim", "Quality MS 2.0 leading textured")] = new(0.058558, 0.063738, 0.061880, 0.062223),
            [("hardware", "rim", "Quality MS 2.0 trailing flat")] = new(0.001078, 0.001088, 0.001088, 0.000000),
            [("hardware", "rim", "Quality MS 2.0 trailing textured")] = new(0.024213, 0.057902, 0.022695, 0.058579),
        };

        public static void Check(string kind, string name, TemporalReviewFigures figures, List<string> failures)
        {
            if (!Before.TryGetValue((TemporalDeviceClass.Name, kind, name), out TemporalReviewFigures before))
            {
                failures.Add($"Missing v21.2.0 baseline for {TemporalDeviceClass.Name} {kind} {name}");
                return;
            }
            CheckValue("error", figures.Error, before.Error);
            CheckValue("temporal error", figures.Temporal, before.Temporal);
            if (kind == "diagonal") CheckValue("change", figures.Change, before.Change);
            if (Math.Abs(figures.Reference - before.Reference) > 2 * Rounding)
                failures.Add($"{name}: reference changed from {before.Reference:0.000000} to {figures.Reference:0.000000}");

            void CheckValue(string metric, double actual, double baseline)
            {
                if (!double.IsFinite(actual) || actual < 0 || actual > (baseline + Rounding) * (1 + Tolerance))
                    failures.Add($"{name}: {metric} {actual:0.000000} above v21.2.0 {baseline:0.000000} plus a tenth");
            }
        }
    }
}
