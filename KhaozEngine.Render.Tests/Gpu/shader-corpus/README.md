# The shipped shader corpus

Two tables of the same 320 artefacts, taken from the same shader sources through two different
shader toolchains, in two different processes.

- `corpus.veldrid-spirv.txt` is the **outgoing** toolchain: `Veldrid.SPIRV` 1.0.15, taken at commit
  `5d839de4` while it was still referenced.
- `corpus.txt` is the **incoming** toolchain: `Silk.NET.Shaderc` + `Silk.NET.SPIRV.Cross` 2.23.0.

The comparison had to happen this way. Section 2.3 result 4 of
`docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md` measured that the two libraries corrupt each other
when both are loaded into one process: both statically link glslang and SPIRV-Tools, the second one
loaded interposes on the first, and the incumbent then reads shuffle operands out of executable
memory or aborts. So the obvious instrument, a test asserting new equals old, is poisoned by its own
existence. Two processes and a diff is what is left.

## Regenerating

`corpus.txt`, any time the shipped shader set changes:

```bash
KE_WRITE_SHADER_CORPUS=1 dotnet test KhaozEngine.Render.Tests --filter ShaderCorpus
```

Add `KE_SHADER_CORPUS_DUMP=<dir>` to also drop all 234 emitted artefacts as files under that
directory. That is what makes a toolchain comparison readable at all: the tables carry hashes, and a
moved hash cannot tell a one-word header change apart from a permuted binding. The dump is not
committed.

`corpus.veldrid-spirv.txt` **cannot be regenerated.** The toolchain that produced it is not
referenced anywhere in the tree any more. To retake it, check out `5d839de4` into a throwaway
worktree and run the command above there. `ShaderCorpusTests` fails if the file goes missing.

## What the row-8 comparison found

Counts, per target, of rows whose hash is unchanged across the swap:

| rows | identical | differing | what moved |
|---|---|---|---|
| 78 SPIR-V | 0 | 78 | the generator word on all 78, code generation on 36 |
| 78 MSL | 36 | 42 | SPIRV-Cross version, in the fragment text |
| 78 HLSL | 36 | 42 | SPIRV-Cross version, in the fragment text |
| 86 layout | 0 | 86 | reflected names only. Every shape is unchanged |

**SPIR-V.** Every module's header word 2, the generator magic, moved from `0x000d000a` to
`0x000d000b`. Generator 13 is *Google Shaderc over Glslang* on both sides, so the outgoing toolchain
was the same generator one version older, and that single word is enough to move all 78 hashes.
42 of the 78 modules are byte-identical apart from it. Magic, the SPIR-V version word
(`0x00010000`, SPIR-V 1.0) and the schema word are identical on all 78, which is the direct evidence
that the pinned target environment and SPIR-V version match what the incumbent compiled for.
The other 36 modules also changed code: the newer glslang contracts multiply-add chains, so
`OpFMul` / `OpFAdd` / `OpFSub` counts fall and `OpExtInst` counts rise (`GroundDecal.fragment` alone
moves 183 `OpFMul` to 156 and 207 `OpExtInst` to 241). Total emission across the whole set grew 368
bytes, 0.09 %.

That is a compiler version difference and **not** an optimisation level difference. Optimisation is
pinned explicitly at `OptimizationLevel.Performance` on `SpirvFrontEndPin`, which is the level
section 2.3 result 3 measured the incumbent to have been using all along. Two different levels cannot
leave 42 of 78 modules byte-identical apart from one header word.

**MSL and HLSL.** 36 of 78 unchanged each, and the split is by stage rather than by program: vertex
text carries over byte-identical while fragment text moves a few bytes either way
(`Beam.fragment` HLSL 1807 to 1809 characters, MSL 1523 to 1493). A SPIRV-Cross version difference in
how it emits the fragment body.

**Layout.** All 86 hashes moved and not one shape did.
`ShaderCorpusTests.TheLayoutsReflectedByBothToolchains_HaveTheSameShapeOnceNamesAreStripped` asserts
that continuously rather than leaving it as a claim in this file. The whole difference is the
reflected name: the outgoing toolchain reported SPIRV-Cross's *fallback* name for the 450 of 624
elements the module does not name, which is the literal SPIR-V id rendered as `_25`, and the incoming
one reports an empty name. Those ids are not stable across a compiler version, and this swap moved
the id bound on 29 of the 78 modules, so reproducing them would reproduce noise. Nothing binds on
them: the engine joins by id, the name-join spike is deleted, and #586 measured that no join on names
is possible.

The pixel truth is the golden suite, not this table.
