using System;
using KhaozEngine.App;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Thrown by an <see cref="IBootStep"/> to fail the boot with a player-facing, localized reason. The
    /// <see cref="BootPipeline"/> catches it, moves to <see cref="BootState.Failed"/>, and surfaces
    /// <see cref="LocalizedMessage"/> on the boot screen (with retry / quit affordances per the options). A step that
    /// throws any OTHER exception also fails the boot, but with the generic
    /// <see cref="BootStrings.ErrorGeneric"/> message (the raw exception text is logged, never shown), so use this
    /// type whenever the failure has a meaningful message for the player - for instance the server-status
    /// min-version gate (<see cref="ServerStatusBootStep"/>) throws it with <see cref="BootStrings.ErrorUpdateRequired"/>.
    /// </summary>
    public sealed class BootStepException : Exception
    {
        /// <summary>The localized message to show for this failure.</summary>
        public LocalizedText LocalizedMessage { get; }

        /// <summary>Create a boot failure carrying a localized <paramref name="message"/>.</summary>
        public BootStepException(LocalizedText message)
            : base(message.ToString())
        {
            LocalizedMessage = message;
        }

        /// <summary>Create a boot failure carrying a localized <paramref name="message"/> and an underlying cause
        /// (logged, never shown).</summary>
        public BootStepException(LocalizedText message, Exception? innerException)
            : base(message.ToString(), innerException)
        {
            LocalizedMessage = message;
        }
    }
}
