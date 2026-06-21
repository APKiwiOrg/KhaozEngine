using System;
using System.IO;

namespace KhaozEngine.Sfx;

/// <summary>
/// Real <see cref="IAudioEncoder"/>. Builds the command plan with <see cref="EncoderArgs"/> and runs each step
/// via the injected <see cref="IProcessRunner"/>, using a temp WAV intermediate for the oggenc path.
/// </summary>
public sealed class FfmpegAudioEncoder : IAudioEncoder
{
    readonly IProcessRunner _runner;

    /// <summary>Creates an encoder over <paramref name="runner"/>.</summary>
    public FfmpegAudioEncoder(IProcessRunner runner) => _runner = runner;

    /// <inheritdoc/>
    public void Encode(EncodeRequest request, VorbisBackend vorbisBackend)
    {
        string intermediateWav = Path.Combine(Path.GetTempPath(), "ke-sfxbake-" + Guid.NewGuid().ToString("N") + ".wav");
        EncoderPlan plan = EncoderArgs.Build(request, vorbisBackend, intermediateWav);
        try
        {
            foreach (EncoderCommand step in plan.Steps)
            {
                ProcessResult res = _runner.Run(step.Exe, step.Args);
                if (res.ExitCode != 0)
                    throw new InvalidOperationException($"{step.Exe} exited {res.ExitCode}: {res.StdErr.Trim()}");
            }
        }
        finally
        {
            try { if (File.Exists(intermediateWav)) File.Delete(intermediateWav); }
            catch (IOException) { /* best-effort */ }
        }
    }
}
