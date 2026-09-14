using System;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that open the machine's real OpenAL output device. They run only with
/// <c>KE_AUDIO_DEVICE_TESTS=1</c>, because CI runners have no audio output and a developer machine should not open
/// its speakers under a plain <c>dotnet test</c>. When the variable is set a missing device is a test error, not a
/// skip, the same strict rule <c>KE_GPU_TESTS=1</c> follows. Everything these tests play is at zero gain.
/// </summary>
public sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KE_AUDIO_DEVICE_TESTS") != "1")
            Skip = "set KE_AUDIO_DEVICE_TESTS=1 to run tests against the real audio output device";
    }
}

/// <summary>
/// Serializes the classes that open a real OpenAL device. The shared state is OpenAL's current context, which is
/// process-global: a second context made current mid-test would turn every AL call in the first into a no-op.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenAlDeviceCollection
{
    public const string Name = "OpenAlDevice";
}
