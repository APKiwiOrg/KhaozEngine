using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Factory helpers for building an <see cref="IBootStep"/> from a delegate, so a game registers its own
    /// asset-warm-up work (textures, kits, audio) without declaring a class. The step's body runs on the boot pump
    /// (the same thread the boot screen renders on), so it may touch GPU / device resources directly. Do a long,
    /// purely CPU-bound warm-up under <c>await Task.Run(...)</c> so the bar keeps animating, then let the continuation
    /// resume on the pump for any device upload. Report progress through the supplied <see cref="IBootProgress"/>.
    /// </summary>
    public static class BootStep
    {
        /// <summary>
        /// Build a step from an async body that reports progress and returns a <see cref="BootStepResult"/> (return
        /// <see cref="BootStepResult.Proceed"/> in the common case). Throw <see cref="BootStepException"/> to fail the
        /// boot with a localized message.
        /// </summary>
        public static IBootStep Create(LocalizedText name, float weight,
            Func<IBootProgress, CancellationToken, Task<BootStepResult>> run)
            => new DelegateBootStep(name, weight, run);

        /// <summary>
        /// Build a step from an async body that reports progress and simply proceeds when it completes (the usual
        /// asset-warm-up shape). Throw <see cref="BootStepException"/> to fail the boot with a localized message.
        /// </summary>
        public static IBootStep Create(LocalizedText name, float weight,
            Func<IBootProgress, CancellationToken, Task> run)
            => new DelegateBootStep(name, weight, async (p, ct) =>
            {
                await run(p, ct).ConfigureAwait(false);
                return BootStepResult.Proceed;
            });

        sealed class DelegateBootStep : IBootStep
        {
            readonly Func<IBootProgress, CancellationToken, Task<BootStepResult>> _run;

            public DelegateBootStep(LocalizedText name, float weight,
                Func<IBootProgress, CancellationToken, Task<BootStepResult>> run)
            {
                if (weight <= 0f) throw new ArgumentOutOfRangeException(nameof(weight), "Step weight must be positive.");
                Name = name;
                Weight = weight;
                _run = run ?? throw new ArgumentNullException(nameof(run));
            }

            public LocalizedText Name { get; }
            public float Weight { get; }

            public Task<BootStepResult> RunAsync(IBootProgress progress, CancellationToken cancellationToken)
                => _run(progress, cancellationToken);
        }
    }
}
