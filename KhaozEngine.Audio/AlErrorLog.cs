using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>
/// Reports the OpenAL error latch after the calls that matter (context setup, buffer queue/unqueue/upload,
/// source play). Without it a device that changes or goes away mid-session (headphones unplugged, a Bluetooth
/// sink dropping, a USB interface resetting) just makes every later AL call a silent no-op, and the game goes
/// quiet with nothing logged.
/// <para>Logging is once per operation name, because the checked calls sit on per-frame paths and a dead device
/// fails every frame. Repeats after the first still count towards <see cref="ErrorCount"/>.</para>
/// <para>Note the AL error latch is per-context and sticky until read, so a code reported here belongs to the
/// checked operation OR to any unchecked call since the last check, not necessarily to the named one.</para>
/// </summary>
internal sealed class AlErrorLog
{
    readonly ILogger _logger;
    readonly HashSet<string> _reported = new();

    public AlErrorLog(ILogger logger) => _logger = logger;

    /// <summary>Non-zero codes seen since construction, including the repeats that were not logged.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Records <paramref name="code"/> (an <c>alGetError</c> / <c>alcGetError</c> result) against
    /// <paramref name="operation"/>. Returns true when it was an actual error, so a caller can bail out.
    /// The zero value of every OpenAL error enum is its "no error" member, so a clean call costs nothing.
    /// </summary>
    public bool Check<TError>(string operation, TError code) where TError : struct, Enum
    {
        if (EqualityComparer<TError>.Default.Equals(code, default)) return false;
        ErrorCount++;
        if (_reported.Add(operation))
            _logger.Error($"OpenAL error after {operation}: {code}. Logged once per operation, later ones are counted only.");
        return true;
    }
}
