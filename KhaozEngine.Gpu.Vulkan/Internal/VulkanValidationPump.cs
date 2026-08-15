using System;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary><c>VkDebugUtilsMessageSeverityFlagBitsEXT</c> reduced to the four rungs that exist, with its own
    /// spelling so this half of the pump names no Silk.NET type and runs under <c>dotnet test</c> on a machine
    /// with no Vulkan loader.</summary>
    internal enum VulkanValidationSeverity
    {
        /// <summary><c>VERBOSE</c>. Never subscribed to: the messenger asks for warning and error only.</summary>
        Verbose = 0,

        /// <summary><c>INFO</c>. Never subscribed to.</summary>
        Info = 1,

        /// <summary><c>WARNING</c>. Logged at WARN.</summary>
        Warning = 2,

        /// <summary><c>ERROR</c>. Logged at ERROR, and the severity <c>strict</c> latches on.</summary>
        Error = 3,
    }

    /// <summary>One validation message, as plain data copied out of the driver's callback structure.</summary>
    /// <param name="Severity">Which rung the layer put it on.</param>
    /// <param name="Id">The layer's own <c>messageIdNumber</c>, which is what the rate limiter keys on alongside
    /// the text.</param>
    /// <param name="IdName">The layer's <c>pMessageIdName</c>, which is the VUID on a validation message and is
    /// the thing worth quoting. Never null: a message with no name reads as its number.</param>
    /// <param name="Text">The message body, already copied to a managed string, because nothing the callback was
    /// handed may outlive the callback.</param>
    internal readonly record struct VulkanValidationMessage(
        VulkanValidationSeverity Severity,
        int Id,
        string IdName,
        string Text);

    /// <summary>
    /// DECISION V-G3 and V-G5's PUMP: the sink every <c>VK_EXT_debug_utils</c> message lands in, rate limited,
    /// with warning and error severities promoted, and <c>strict</c>'s latch. Driven by
    /// <see cref="VulkanDebugMessenger"/>, which owns the native messenger and the CDECL callback, and never
    /// created at all when <c>KE_VULKAN_VALIDATION</c> is off.
    /// <para>
    /// <b>IT LOGS AND NEVER THROWS, WHICH IS THE DECISION.</b> The incumbent's callback throws a managed exception
    /// and calls <c>Debugger.Break()</c> from inside a native driver callback. Unwinding a managed exception
    /// through native driver frames is not a diagnostic: it is undefined behaviour that destroys the stack the
    /// diagnostic was about, on a code path that only runs when something has already gone wrong.
    /// <c>strict</c>'s throw is what that behaviour is FOR, and it happens at a controlled point through
    /// <see cref="ThrowIfLatched"/> rather than inside the callback.
    /// </para>
    /// <para>
    /// <b>PROMOTED TO WARN AND ERROR.</b> A validation warning is a real defect report, and burying it at INFO is
    /// how a session with validation on produces nothing anybody reads. Error severity goes to ERROR, unlike the
    /// Direct3D 11 pump which caps at WARN, and the difference is what the two layers mean: a Direct3D
    /// debug-layer corruption message is frequently a driver's opinion about something benign, while a Vulkan
    /// validation error is a spec violation with a VUID number attached.
    /// </para>
    /// <para>
    /// <b>EVERYTHING HERE IS DEVICE-FREE</b>, over <see cref="VulkanValidationMessage"/> values, so the promotion,
    /// the rate limit, the strict latch and the controlled throw all run on a machine with no Vulkan loader. The
    /// native half is the messenger, which is the only part that cannot be tested off a driver.
    /// </para>
    /// </summary>
    internal sealed class VulkanValidationPump
    {
        readonly VulkanValidationMode _mode;
        readonly VulkanValidationRateLimit _limit;
        readonly ILogger _log;

        int _errorCount;
        string? _latched;

        /// <param name="mode">The rung this session is on. Only <see cref="VulkanValidationMode.Strict"/> latches,
        /// and the others log and carry on.</param>
        /// <param name="limit">The rate limit, or null for the defaults.</param>
        /// <param name="logger">The sink, or null for this type's own category logger. Present so a test can
        /// assert what was logged and at which level, which is the half of this type worth asserting.</param>
        /// <remarks>
        /// THE FALLBACK LOGGER IS RESOLVED HERE, PER PUMP, AND NOT ONCE INTO A STATIC FIELD (#565). At the time
        /// that mattered: a static <c>Log.For&lt;VulkanValidationPump&gt;()</c> is captured at type
        /// initialization, so which logger it held forever depended on whether the type was touched before or
        /// after the process called <c>Log.Configure</c>, and the loser of that race was a no-op logger. Every
        /// message this pump exists to surface was then dropped by a process that HAD configured a sink,
        /// invisibly, because a dropped log line and a clean run look identical.
        /// <para>
        /// 17.36.2 FIXED THAT AT THE FACADE (#616), so a static field would now be correct too: <c>Log.For</c>
        /// hands back a logger bound to the category and not to a manager, and it finds the configured manager
        /// on every call. This stays per-pump anyway. It costs one <c>Log.For</c> call next to creating a Vulkan
        /// instance, and the ctor parameter above (the reason the fallback is resolved at all) is per-pump by
        /// nature.
        /// </para>
        /// </remarks>
        internal VulkanValidationPump(VulkanValidationMode mode, VulkanValidationRateLimit? limit = null,
            ILogger? logger = null)
        {
            _mode = mode;
            _limit = limit ?? new VulkanValidationRateLimit();
            _log = logger ?? Log.For<VulkanValidationPump>();
        }

        /// <summary>How many messages the rate limit has refused. The number that stops a truncated log reading as
        /// a quiet one.</summary>
        internal int Suppressed => _limit.Suppressed;

        /// <summary>How many ERROR-severity messages have arrived, counted whether or not the rate limit let them
        /// be logged. Counted separately from the limiter on purpose: a session that suppressed 400 copies of one
        /// error still saw 400 errors, and a run reporting zero because the log went quiet would be the exact
        /// misreading the limiter's notes exist to prevent.</summary>
        internal int ErrorCount => Volatile.Read(ref _errorCount);

        /// <summary>True when <c>strict</c> has latched an error and <see cref="ThrowIfLatched"/> will throw at
        /// the next controlled point.</summary>
        internal bool IsLatched => Volatile.Read(ref _latched) != null;

        /// <summary>
        /// Take one message. Called from the driver's callback thread, so it must never throw and never block for
        /// long: the caller is inside a Vulkan call.
        /// <para>
        /// The whole body is inside a catch, which is unusual and is right here. A logger that faults inside a
        /// native callback would unwind through driver frames, which is the exact failure V-G5 is about, so the
        /// last resort is to lose the message rather than the process.
        /// </para>
        /// </summary>
        internal void Report(in VulkanValidationMessage message)
        {
            try
            {
                ReportCore(message);
            }
            catch
            {
                // Deliberately empty and deliberately unlogged: the logger is the thing that just failed, so the
                // only honest action left is to return into the driver without unwinding.
            }
        }

        /// <summary>
        /// THE CONTROLLED POINT. Throws when <c>strict</c> has latched an error, naming the site that reached the
        /// check and the message that latched. A no-op on every other rung and on a session with no errors.
        /// <para>
        /// Called after device creation and at <c>WaitForIdle</c> by this row, and by each later row at its own
        /// natural boundary (the submit and the present). Deliberately NOT called from <c>Dispose</c>: throwing
        /// out of teardown replaces a diagnostic with a second failure at the moment the first one mattered, and
        /// the whole point of latching is that the error is already recorded and already logged.
        /// </para>
        /// </summary>
        /// <param name="site">Where the check was made, e.g. <c>vkCreateDevice</c> or <c>WaitForIdle</c>.</param>
        /// <exception cref="InvalidOperationException">An error-severity validation message arrived and this
        /// session is on the <c>strict</c> rung.</exception>
        internal void ThrowIfLatched(string site)
        {
            string? latched = Volatile.Read(ref _latched);
            if (latched is null) return;

            throw new InvalidOperationException(
                $"Vulkan validation reported an error and {VulkanValidation.EnvVarName}=strict, so this run stops "
                + $"at {site}, which is the first controlled point after it. The message was: {latched}. This "
                + "throw is deliberately here and not in the driver callback that saw it, because unwinding a "
                + "managed exception through native driver frames destroys the stack the diagnostic was about. "
                + $"Set {VulkanValidation.EnvVarName}=1 to log validation errors and carry on.");
        }

        void ReportCore(in VulkanValidationMessage message)
        {
            if (message.Severity == VulkanValidationSeverity.Error)
            {
                Interlocked.Increment(ref _errorCount);
                if (VulkanValidation.ThrowsOnError(_mode))
                {
                    // CompareExchange, so the FIRST error is the one reported. A later one is a consequence of
                    // the first as often as it is a second defect, and a latch that kept overwriting would name
                    // whichever error happened to be last rather than the one worth looking at.
                    Interlocked.CompareExchange(ref _latched, Format(message), null);
                }
            }

            if (!_limit.Admit(message, out string? note))
            {
                if (note != null) _log.Warn(note);
                return;
            }

            string body = Format(message);
            if (message.Severity == VulkanValidationSeverity.Error) _log.Error(body);
            else _log.Warn(body);
        }

        // The VUID first, because that is what a reader searches for. The numeric id is kept because a message
        // with no name still has to be tellable from another one in the rate limiter's own note.
        static string Format(in VulkanValidationMessage message)
        {
            string name = string.IsNullOrWhiteSpace(message.IdName)
                ? "message " + message.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : message.IdName;
            return $"Vulkan validation [{message.Severity}] {name}: {message.Text}";
        }
    }
}
