using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace KhaozEngine.Catalog;

/// <summary>
/// The one field the active <see cref="ContentRuntime"/> lives in, spec 9.7. A version is published with a
/// <see cref="Volatile.Write{T}"/> and read with a <see cref="Volatile.Read{T}"/>, and there is no lock
/// anywhere on the path.
/// <para>
/// <b>A reader takes the reference ONCE at the top of whatever it is doing and uses that instance
/// throughout</b>, so a swap in the middle of an operation cannot hand it a half-old half-new answer. That is
/// the whole discipline, and it works because a <see cref="ContentRuntime"/> and everything reachable from
/// one is immutable after construction: no array is written after the load, so there is no torn read and no
/// barrier is needed beyond the write that publishes the new instance. The one field that is written later is
/// the registered load-index map of boot step 7b, which the boot runs BEFORE it publishes and which carries
/// its own volatile pair for the other order the public API allows.
/// </para>
/// <para>
/// <b>v1 never swaps at runtime</b>, because a new version applies at server restart (contracts 1.3 item 8).
/// The field and the volatile pair exist anyway, for two reasons: a test swaps a runtime to exercise a
/// fixture, and a later live-apply phase needs exactly this shape and nothing else. The cost of building it
/// now is two lines.
/// </para>
/// </summary>
public sealed class ContentRuntimeHolder
{
    ContentRuntime? _current;

    /// <summary>
    /// The active version, or a throw when the boot has not published one. There is deliberately no fallback
    /// to a default catalog: a server with no content FAILS (spec 9.6, contracts 10.5), because a silent
    /// fallback serves content no version names and an outage is at least noticed.
    /// </summary>
    /// <exception cref="InvalidOperationException">No version has been published into this holder.</exception>
    public ContentRuntime Current => Volatile.Read(ref _current)
        ?? throw new InvalidOperationException(
            "The content runtime is not loaded. A version is published at boot step 9, and there is no fallback to code defaults.");

    /// <summary>True once a version has been published, asked without throwing.</summary>
    public bool IsLoaded => Volatile.Read(ref _current) is not null;

    /// <summary>The active version, or false. The non-throwing form of <see cref="Current"/>.</summary>
    public bool TryGetCurrent([MaybeNullWhen(false)] out ContentRuntime runtime)
    {
        runtime = Volatile.Read(ref _current);
        return runtime is not null;
    }

    /// <summary>
    /// Boot step 9: publish the runtime. One <see cref="Volatile.Write{T}"/> of a whole instance, which is
    /// the entire swap, because the new version was built beside the old one rather than into it.
    /// </summary>
    /// <param name="runtime">The version to make active.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public void Publish(ContentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Volatile.Write(ref _current, runtime);
    }
}
