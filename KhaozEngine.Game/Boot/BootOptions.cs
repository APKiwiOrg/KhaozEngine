using System;
using System.Collections.Generic;
using KhaozEngine.ServerStatus;
using KhaozEngine.Updates;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Turn-key configuration for the boot pipeline: which built-in steps run (each individually optional), the
    /// game's own asset-warm-up steps, the failure affordances, and the presentation theme. <see cref="BuildSteps"/>
    /// assembles the ordered step list (update -&gt; server-status -&gt; game steps), skipping any built-in that is not
    /// configured. Nothing configured (no update service, no server-status client, no game steps) yields an empty
    /// pipeline that completes immediately - the boot screen flashes and hands off. Pass an instance to
    /// <see cref="BootScreen.Create"/>.
    /// </summary>
    public sealed class BootOptions
    {
        /// <summary>The update service to gate on, or null to skip the update step entirely. When set, a
        /// <see cref="UpdateBootStep"/> runs first.</summary>
        public UpdateService? UpdateService;

        /// <summary>Weight of the update step's slice of the bar (default 1).</summary>
        public float UpdateStepWeight = 1f;

        /// <summary>Bounds the update version check so a slow / unreachable feed degrades to proceeding (null uses the
        /// service default).</summary>
        public TimeSpan? UpdateCheckTimeout;

        /// <summary>The server-status client to consult, or null to skip the server-status step (off unless a status
        /// endpoint is configured). When set, <see cref="LocalClientVersion"/> is required.</summary>
        public ServerStatusClient? ServerStatusClient;

        /// <summary>This build's version, compared against the server's minimum. Required when
        /// <see cref="ServerStatusClient"/> is set.</summary>
        public string? LocalClientVersion;

        /// <summary>Weight of the server-status step's slice of the bar (default 1).</summary>
        public float ServerStatusStepWeight = 1f;

        /// <summary>Which evaluated states fail the boot (default: just <see cref="ServerStatusState.UpdateRequired"/>).</summary>
        public IReadOnlySet<ServerStatusState>? ServerStatusBlockingStates;

        /// <summary>The game's own loading steps (textures, kits, audio), appended AFTER the built-in steps. Build
        /// them with <see cref="BootStep"/>.Create.</summary>
        public IReadOnlyList<IBootStep> GameSteps = Array.Empty<IBootStep>();

        /// <summary>Offer a retry button / Enter shortcut in the failure state (default true).</summary>
        public bool AllowRetryOnFailure = true;

        /// <summary>Offer a quit button / Escape shortcut in the failure state (default true).</summary>
        public bool AllowQuitOnFailure = true;

        /// <summary>The boot screen look and layout (default <see cref="BootScreenTheme.Default"/>).</summary>
        public BootScreenTheme Theme = BootScreenTheme.Default;

        /// <summary>
        /// Assemble the ordered step list from the configured options: the update step (if <see cref="UpdateService"/>
        /// is set), then the server-status step (if <see cref="ServerStatusClient"/> is set), then the
        /// <see cref="GameSteps"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="ServerStatusClient"/> is set without
        /// <see cref="LocalClientVersion"/>.</exception>
        public IReadOnlyList<IBootStep> BuildSteps()
        {
            var steps = new List<IBootStep>();
            if (UpdateService is not null)
                steps.Add(new UpdateBootStep(UpdateService, UpdateStepWeight, UpdateCheckTimeout));
            if (ServerStatusClient is not null)
            {
                if (string.IsNullOrEmpty(LocalClientVersion))
                    throw new InvalidOperationException(
                        "BootOptions.LocalClientVersion is required when ServerStatusClient is set.");
                steps.Add(new ServerStatusBootStep(
                    ServerStatusClient, LocalClientVersion, ServerStatusStepWeight, ServerStatusBlockingStates));
            }
            steps.AddRange(GameSteps);
            return steps;
        }
    }
}
