using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE PLACE THAT ANSWERS "can this machine build a native Metal device", for the rows that need a SECOND
    /// device beside the suite's own. The lifecycle rows each carry their own inline copy of this pair because
    /// they hold an <c>ITestOutputHelper</c> field and answer through a
    /// <c>[SupportedOSPlatformGuard]</c> method, and this is the shared form for the rows that do not.
    /// <para>
    /// IT IS A DORMANT RETURN RATHER THAN A SKIP, which is phase 3's row-19 lesson: under <c>KE_GPU_TESTS=1</c>
    /// the Windows and Linux legs run this assembly in strict mode where a skip is a failure, so a row that has no
    /// device to talk to records the reason and asserts nothing.
    /// </para>
    /// <para>
    /// AND <c>KE_METAL_REQUIRED=1</c> IS THE WAY OUT OF A DORMANT ROW LOOKING LIKE A PASSING ONE, which is the
    /// same lesson taken one step further and is phase 3's <c>VulkanDormancy</c> inherited by name. A dormant
    /// return is right on a developer box and on every CI leg but the <c>metal-native</c> one. On THAT leg it is
    /// a test that asserted nothing and reported green, which the zero-skipped criterion of rollout gate 2 cannot
    /// see, because the row did not skip. With the variable set every answer below THROWS instead, naming what
    /// the probe objected to, so a driver or runner-image regression reds the leg built to catch it. Unset, every
    /// row goes dormant exactly as before.
    /// </para>
    /// </summary>
    static class MetalDormancy
    {
        /// <summary>The variable a leg sets to declare it must have a native Metal device. A constant so the
        /// workflow, the test file headers and the failure message cannot drift apart on the spelling.</summary>
        internal const string RequiredVariable = "KE_METAL_REQUIRED";

        /// <summary>Whether this leg declared a native Metal device mandatory. Compared against <c>"1"</c>
        /// exactly, the spelling <c>GpuFactAttribute</c> already uses for <c>KE_GPU_TESTS</c>.</summary>
        internal static bool IsRequired => Environment.GetEnvironmentVariable(RequiredVariable) == "1";

        /// <summary>True when a native Metal device can be created here. Writes the reason it cannot to
        /// <paramref name="output"/>, so a dormant run says which of the two machine facts it hit, and THROWS
        /// rather than answering false when <see cref="IsRequired"/>.
        /// <para>
        /// A <c>[SupportedOSPlatformGuard]</c> rather than a plain bool, and it is honest: the first thing this
        /// asks is <c>KhaozEngineMetal.IsPlatformSupported</c>, so a true answer really does imply macOS. That is
        /// what lets a caller read a macOS-only member after it without CA1416 firing, which the lifecycle rows
        /// already do through their own inline copy of this pair.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">This machine has no native Metal device and the leg set
        /// <see cref="RequiredVariable"/>.</exception>
        [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
        internal static bool NativeDeviceAvailable(ITestOutputHelper output)
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                ThrowIfRequired("this is not macOS at all");
                output.WriteLine("dormant: not macOS, so there is no native Metal device to compare.");
                return false;
            }

            string? missing = MissingRequirement();
            if (missing is null) return true;

            ThrowIfRequired(missing);
            output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }

        /// <summary>
        /// Turn a refusal into a hard failure where the leg declared a device mandatory, and do nothing at all
        /// otherwise. Called by this type and by the rows that carry their own inline copy of the pair above, so
        /// the variable has exactly one reader and one message however many places go dormant.
        /// </summary>
        /// <param name="probeAnswer">What the machine objected to, in the probe's own words where there is one.</param>
        internal static void ThrowIfRequired(string? probeAnswer)
        {
            if (!IsRequired) return;
            throw new InvalidOperationException(RefusalMessage(probeAnswer));
        }

        /// <summary>
        /// The pure half: what a refusal reads like, given the machine's own answer. Split out so the message is
        /// assertable on a machine with no Metal at all, which is every Windows and Linux leg in the matrix and
        /// therefore most of the machines that will ever run this assembly.
        /// </summary>
        internal static string RefusalMessage(string? probeAnswer)
            => $"{RequiredVariable}=1 says this leg must have a native Metal device, and this machine cannot "
                + "provide one: "
                + (probeAnswer ?? "no reason was recorded, so the refusal came from outside the requirement walk")
                + ". This is a hard failure rather than a dormant row because a dormant row on THIS leg is a pass "
                + "with no assertions in it, which the zero-skipped gate cannot see. Unset "
                + $"{RequiredVariable} to let these rows go dormant again.";

        // Split out under the guard so CA1416 can see that the probe is only ever read on macOS. The caller's own
        // IsPlatformSupported check is what makes that true, and the analyzer reads the guard at the call site.
        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        static string? MissingRequirement() => MetalSupportProbe.MissingRequirement();
    }

    /// <summary>
    /// THE OTHER MACHINE FACT A ROW CAN HAVE TO STAND DOWN FOR, and it is nothing to do with having a device:
    /// whether this PROCESS was launched with Metal's API validation layer armed
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/591">#591</see>). The <c>metal-native</c> CI
    /// leg sets <c>MTL_DEBUG_LAYER=1</c> as a job variable on every run, which is decision M-T7's first tier.
    /// <para>
    /// <b>THE DEFAULT ERROR MODE IS ASSERT, MEASURED RATHER THAN ASSUMED, AND THAT IS THE WHOLE PROBLEM.</b>
    /// #591 recorded this as a property of <c>MTL_DEBUG_LAYER_ERROR_MODE=assert</c>, which reads as "do not set
    /// that and you are fine". Row 19 measured otherwise: plain <c>MTL_DEBUG_LAYER=1</c> aborted the test HOST
    /// on the first objection, taking the whole run's signal with it (5956 of 6039 rows reported, then "Test Run
    /// Aborted"). <c>MTL_DEBUG_LAYER_ERROR_MODE=nslog</c> stops the abort and was measured to report NOTHING to
    /// the captured stream, which would be a tier that can neither fail nor testify, so it is not taken. The leg
    /// runs the layer at its default and the rows that legitimately provoke it stand down here instead.
    /// </para>
    /// <para>
    /// <b>A ROW ONLY BELONGS HERE IF IT PROVOKES VALIDATION ON PURPOSE.</b> Reproducing a known mis-binding for
    /// a measurement, or recording ABI calls on an encoder with no pipeline bound, are legitimate things for a
    /// test to do and illegitimate things for a shipped frame to do, so validation is right and the row is
    /// right, and they cannot share a process. A row standing down because a real defect makes it uncomfortable
    /// would be this helper doing exactly the damage it exists to prevent.
    /// </para>
    /// </summary>
    static class MetalValidationDormancy
    {
        /// <summary>
        /// True when Metal's API validation is armed in this process, which on macOS means the NATIVE
        /// environment carried the variable at launch, since the runtime reads it before managed code exists.
        /// False off macOS and false on an ordinary developer run.
        /// </summary>
        internal static bool ArmedAtLaunch
        {
            get
            {
                if (!KhaozEngineMetal.IsPlatformSupported) return false;

                MetalValidationArming arming = Capture();
                // Armed with the in-process flag clear is exactly the launch-armed shape: a variable this
                // process set for itself never reached the Metal runtime and cannot be what is validating.
                return arming.Armed != MetalValidationMode.Off && !arming.DebugLayerSetInProcessOnly;
            }
        }

        /// <summary>
        /// True when the SHADER rung is armed, which is <c>MTL_SHADER_VALIDATION</c> on top of the API layer and
        /// the scheduled sweep's tier. A separate question from <see cref="ArmedAtLaunch"/> because it costs
        /// different things: in-shader bounds checking instruments every shader, which moves TIMING, and a row
        /// that provokes a CPU-versus-GPU race can stop provoking anything at all under it.
        /// </summary>
        internal static bool ShaderRungArmedAtLaunch
            => KhaozEngineMetal.IsPlatformSupported && Capture().Armed == MetalValidationMode.Shaders;

        /// <summary>
        /// Stand down when validation is armed, saying so on <paramref name="output"/> and naming the row's own
        /// reason, and answer false otherwise so the caller carries on. The reason is the caller's because it is
        /// the part a reader needs and the part this type cannot know.
        /// </summary>
        internal static bool StandDown(ITestOutputHelper output, string whatItProvokes)
        {
            if (!ArmedAtLaunch) return false;

            output.WriteLine(
                "dormant: Metal API validation is armed in this process, and this row " + whatItProvokes
                + ". The layer's default error mode is assert, so running it here would abort the test host "
                + "rather than fail a row. See https://github.com/APKiwiOrg/KhaozEngine/issues/591.");
            return true;
        }

        /// <summary>
        /// Stand down when the SHADER rung is armed, for a row whose provocation the instrumentation removes
        /// rather than one the layer objects to. Separate from <see cref="StandDown"/> because the two say
        /// different things and a reader of the log should be able to tell them apart: this one is "the
        /// measurement stopped being possible", not "the layer would abort".
        /// </summary>
        internal static bool StandDownForShaderRung(ITestOutputHelper output, string whatItNeeds)
        {
            if (!ShaderRungArmedAtLaunch) return false;

            output.WriteLine(
                "dormant: MTL_SHADER_VALIDATION is armed in this process, which instruments every shader and "
                + "moves the CPU-versus-GPU timing this row depends on. It needs " + whatItNeeds
                + ", and under in-shader bounds checking that stops happening, so the row would fail on its own "
                + "provocation rather than on the thing it asserts.");
            return true;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        static MetalValidationArming Capture() => MetalValidationReader.Capture();
    }
}
