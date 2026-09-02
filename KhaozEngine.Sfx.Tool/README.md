# KhaozEngine.Sfx.Tool

The `ke-sfxbake` dotnet tool. Manifest-driven bulk SFX generation + bake: reads a per-game
`sfx.manifest.jsonc`, generates each effect via the ElevenLabs sound-effects API, encodes with
ffmpeg (libvorbis) or oggenc, and writes the result into the game's asset tree. `.sfxmeta` hash
sidecars make re-runs idempotent, so unchanged entries are skipped. Author-time dev tool, not a
runtime package.

Install:

```bash
dotnet tool install --global KhaozEngine.Sfx.Tool
```

## Usage

```bash
ke-sfxbake bake path/to/sfx.manifest.jsonc            # generate new/changed, skip unchanged
ke-sfxbake bake path/to/sfx.manifest.jsonc --dry-run  # plan + estimated credits, spends nothing
ke-sfxbake bake path/to/sfx.manifest.jsonc --force    # regenerate everything
```

Options:

- `--dry-run` - print the plan + estimated credits, generate nothing
- `--force` - regenerate every entry, ignoring sidecars
- `--model <id>` - override the ElevenLabs model id
- `--source-format <f>` - override the API source output_format (e.g. `pcm_44100`)

## Manifest

```jsonc
// sfx.manifest.jsonc - paths resolve relative to this file
{
  "sounds": [
    { "key": "ui/confirm", "prompt": "crisp sci-fi UI confirm blip, short synth tail",
      "durationSeconds": 1.2, "out": "Assets/Sfx/ui/confirm.ogg" },   // mono OGG (default)
    { "key": "ui/click", "prompt": "soft latency click", "format": "wav",
      "out": "Assets/Sfx/ui/click.wav" },                             // 16-bit PCM WAV for one-shots
  ],
}
```

## Environment

- `ELEVENLABS_API_KEY` - required for a real bake, not for `--dry-run`.
- OGG output needs a Vorbis encoder on PATH: ffmpeg built with libvorbis, or `oggenc`
  (vorbis-tools). WAV-only manifests need neither.
- An encoder gets 5 minutes to finish (`SystemProcessRunner.DefaultTimeout`, overridable through the
  constructor). Past that it is killed with anything it started and the bake fails with a `TimeoutException`
  instead of parking forever. Both of the encoder's output pipes are drained concurrently, so a chatty ffmpeg
  cannot wedge the bake by filling the stderr pipe buffer.
