using System;
using KhaozEngine.Game;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // GameApp always owns a real AppWindow (see GameAppResumeTests), so the instance property itself is not
    // constructible headless. The DECISION it lazily caches is factored into the pure, internal
    // GameApp.CreateJobScheduler(disabled, degreeOfParallelism), which mirrors BuildDiagnosticsTheme: fully
    // headless-testable without standing up a window. GameApp.JobScheduler's own laziness/caching (one field,
    // `_jobScheduler ??= CreateJobScheduler(...)`) is the sample-verified wiring on top, not covered here.
    public sealed class GameAppJobSchedulerTests
    {
        [Fact]
        public void Enabled_by_default_builds_a_thread_pool_scheduler()
        {
            IJobScheduler scheduler = GameApp.CreateJobScheduler(disabled: false, degreeOfParallelism: null);
            Assert.IsType<ThreadPoolJobScheduler>(scheduler);
        }

        [Fact]
        public void Default_sizing_is_processor_count_minus_one_floored_at_one()
        {
            var scheduler = (ThreadPoolJobScheduler)GameApp.CreateJobScheduler(disabled: false, degreeOfParallelism: null);
            int expected = Math.Max(1, Environment.ProcessorCount - 1);
            Assert.Equal(expected, scheduler.MaxDegreeOfParallelism);
        }

        [Fact]
        public void Explicit_degree_of_parallelism_is_honoured()
        {
            var scheduler = (ThreadPoolJobScheduler)GameApp.CreateJobScheduler(disabled: false, degreeOfParallelism: 3);
            Assert.Equal(3, scheduler.MaxDegreeOfParallelism);
        }

        [Fact]
        public void Disable_flag_returns_the_deterministic_single_threaded_scheduler()
        {
            IJobScheduler scheduler = GameApp.CreateJobScheduler(disabled: true, degreeOfParallelism: null);
            Assert.IsType<SingleThreadedJobScheduler>(scheduler);
        }

        [Fact]
        public void Disable_flag_wins_even_when_a_degree_of_parallelism_is_also_set()
        {
            IJobScheduler scheduler = GameApp.CreateJobScheduler(disabled: true, degreeOfParallelism: 8);
            Assert.IsType<SingleThreadedJobScheduler>(scheduler);
        }

        [Fact]
        public void Default_constructed_options_keep_the_scheduler_on()
        {
            // A raw `new GameAppOptions { ... }` (no For) must still enable the scheduler - DisableJobScheduler is
            // inverted so the default-zero struct value keeps it on, same convention as DisableDiagnosticsOverlay.
            var opts = default(GameAppOptions);
            Assert.False(opts.DisableJobScheduler);
            Assert.Null(opts.JobSchedulerDegreeOfParallelism);
        }
    }
}
