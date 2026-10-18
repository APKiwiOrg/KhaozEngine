# KhaozEngine.CodeHealth.Analyzers

Compile-time enforcement of the fleet's file-size ratchet and attributed binary32 kernel inventory.
The file-size rules mirror `scripts/check-file-size.sh` in game-template. The binary32 rules apply
only to attributed code. Both run in the build and the IDE.

## Diagnostics

| Id | Severity | Fires when |
|----|----------|------------|
| KESIZE001 | Error | A file listed in `.filesize-baseline` exceeds its recorded line count. A baselined file may shrink freely, it may never grow. |
| KESIZE002 | Error | A file NOT in the baseline exceeds the cap (default 800 lines). |
| KEB32001 | Error | A kernel uses a binary, unary, increment or compound-assignment operator with a float, double or decimal operand, including comparisons and equality. Move it into the arithmetic home. |
| KEB32002 | Error | A kernel performs a nonconstant numeric conversion from or to float, double or decimal. Move it into the arithmetic home. |
| KEB32003 | Error | A kernel invokes Math or MathF with floating parameters, or invokes a Single, Double, BitConverter, System.Numerics or System.Runtime.Intrinsics member, including operators. Call the arithmetic home instead. |
| KEB32004 | Error | A kernel invokes an unmarked method or accesses an unmarked computed property declared in source. Mark the callee as a kernel or move its arithmetic into the arithmetic home. |

Line count is `wc -l` parity: the number of newline characters. Exclusions match the script:
`obj/`, `bin/`, `vendor/` path segments and `.Designer.cs`, `.g.cs`, `.generated.cs`,
`.AssemblyInfo.cs` suffixes, plus anything Roslyn marks as generated code.

Both file-size messages carry their own remediation inline rather than only in the descriptor description,
because MSBuild prints the message and not the description: guidance an agent cannot see is guidance
that does not exist. The remediation routes a legitimate case to "ask the repo owner", never to a
split that exists only to silence the error.

## Binary32 kernel attribute contract

`Binary32KernelAttribute` opts a method, constructor, accessor, local function or lambda into the
inventory rules. An attribute on a containing method or type applies to its nested kernel members.
The attribute is recognized by simple type name in any namespace, including shared attribute
assemblies. The analyzer does not supply the attributes. A type carrying
`Binary32ArithmeticAttribute` is the hand-reviewed arithmetic home. Its members and nested types
are exempt from all four rules, and kernels may call them. Kernel membership is cached per
compilation. Compilations without a `Binary32KernelAttribute` type register no operation or syntax
actions, even if an arithmetic-home attribute exists.

Integer arithmetic and conversions, bool logic, float selection (`c ? a : b`) and compile-time
constant numeric conversions remain legal. So do object and exception construction, fields, auto
properties, array, span and `IReadOnlyList<T>` indexers. The trusted System array, span and memory
APIs `AsSpan`, `Slice`, `CopyTo`, `TryCopyTo`, `Clear`, `Fill` and `Overlaps` are allowed, together
with `object.ReferenceEquals`, `ArgumentNullException.ThrowIfNull` and integer Math overloads.
Implicit array, span and memory storage-view conversions also remain legal.
Unrelated methods with those names are still checked. Computed properties must mark the getter or
setter that the kernel actually reaches. Diagnostic messages carry their own rule and remediation.

The operation order and device-exactness obligations are defined in the
[equipment coverage kernel contract](../docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md).

## Exemptions (14.8.0)

A baseline line of the form `exempt <path>` takes a file out of both rules: no frozen size, no cap,
no diagnostic. Put the reason on a `#` line above it, since the path is the rest of the line.

```
# size is content, not structure: regenerated wholesale, one row per ISO country code
exempt MyGame.Content/Generated/CountryCodes.cs
```

This exists for files whose size is CONTENT rather than STRUCTURE (a generated lookup table, an
embedded data blob), where freezing the size only pressures the next contributor into splitting at an
arbitrary line, which is the failure the ratchet exists to prevent. It is not for a test fixture that
accreted cases or a screen or frame-loop class: those should be split by responsibility, and the
ratchet pressing you to do it is working correctly.

**The test is growth, not syntax.** Does the file grow only when the DATA grows, or also whenever its
subsystem gains a feature? Answer from `git log`, not from what the file looks like. "It is all
constants" is not the test: the engine's own `ShaderSources.cs` was 2624 lines of nothing but
`const string` with no logic, and was still the wrong candidate, because it grew with every renderer
feature. Constants that encode behaviour are structure. It was split by render domain in 14.8.1
rather than exempted.

An exempt entry wins over a numeric entry for the same path in either order. `--init` and
`--preview` never emit one, and `--update` preserves the ones you wrote, so an exemption is only ever
a deliberate hand-edit. A consumer pinned below 14.8.0 does not understand the line and reads the
path as unlisted, so it reports a cap violation instead: loud and correctly shaped rather than
silently wrong, but adopt exempt lines only once you are on a pin that understands them.

## Adoption

Zero-touch. The package ships buildTransitive props that discover the consuming repo's
`.filesize-baseline` by walking up from each project directory and hand it to the analyzer as an
AdditionalFile. A repo with no baseline has not adopted the ratchet and the file-size analyzer stays silent.
Create a baseline with `scripts/check-file-size.sh --init`.

Knobs, both MSBuild properties: `KhaozFileSizeCap` overrides the 800-line cap for unlisted files,
`KhaozFileSizeBaselineDir` overrides the discovered baseline directory. One baseline per repo.

Blessing a deliberately large new file, or deliberate growth, is a hand-edit of
`.filesize-baseline`. There is no environment-variable escape hatch on purpose. In the engine and in
game-template that hand-edit is additionally gated by a write-time agent hook that turns it into a
confirmation prompt, so an agent cannot raise a frozen size or grant an exemption silently inside a
large diff. Ratcheting DOWN stays free: `scripts/check-file-size.sh --update` can only lower or drop
entries, never raise one.

Flows to consumers via all four umbrellas (`Foundation`, `Game2D`, `Game3D`, `Server`).
