# KhaozEngine.CodeHealth.Analyzers

Compile-time file-size and test-health ratchets in the build and IDE, and an opt-in floating point rule.
The semantic authority for the file-size ratchet (the god-class guard) is `scripts/check-file-size.sh` in
game-template. The test-health ratchet counts brittle assertions and non-public reflection against a
separate baseline. KEFP001 checks for source-visible subnormal `float` constants that can reach the JIT. See
[Subnormal float constants](#subnormal-float-constants).

## Diagnostics

| Id | Severity | Fires when |
|----|----------|------------|
| KESIZE001 | Error | A file listed in `.filesize-baseline` exceeds its recorded line count. A baselined file may shrink freely, it may never grow. |
| KESIZE002 | Error | A file NOT in the baseline exceeds the cap (default 800 lines). |
| KEFP001 | Error, opt-in | A `float` step or constant puts a subnormal value where the JIT can fold it. Silent unless `KhaozSubnormalFloatRule` is set. |

Line count is `wc -l` parity: the number of newline characters. Exclusions match the script:
`obj/`, `bin/`, `vendor/` path segments and `.Designer.cs`, `.g.cs`, `.generated.cs`,
`.AssemblyInfo.cs` suffixes, plus anything Roslyn marks as generated code.

Both messages carry their own remediation inline rather than only in the descriptor description,
because MSBuild prints the message and not the description: guidance an agent cannot see is guidance
that does not exist. The remediation routes a legitimate case to "ask the repo owner", never to a
split that exists only to silence the error.

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
AdditionalFile. A repo with no baseline has not adopted the ratchet and the analyzer stays silent.
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

## Test-health ratchet

KETEST rules run only in compilations that reference xunit and have a `.testhealth-baseline` supplied
as an AdditionalFile. Without that file they stay silent, including in report mode. Consumers adopt
by adding the file. The package's buildTransitive props discover it by walking up from each project
directory. `KhaozTestHealthBaselineDir` overrides that discovered directory independently of the
file-size baseline.

| Id | Mode | Counts and exclusions |
|----|------|-----------------------|
| KETEST001 | Error above the per-file allowance | Each `Exception.Message` access inside `Assert.Contains`, `Equal`, `NotEqual`, `StartsWith`, `EndsWith`, `Matches`, `True` or `False`. `DoesNotContain` and `DoesNotMatch` absence checks never count because they are disclosure contracts. Unrelated properties named `Message` do not count. Prefer the exception type and structured members such as parameter name, error code or status. |
| KETEST002 | Error above the per-file allowance | Each `BindingFlags.NonPublic` reference and each `UnsafeAccessor` attribute application. Public-only reflection does not count. Prefer the public API or an internal seam exposed through `InternalsVisibleTo`. |
| KETEST003 | Report-only review aid | Test methods whose every direct assertion runs inside a loop or `Assert.All` over a source that is not provably non-empty. Proven non-empty loops and tests with an assertion outside those loops do not count. Assert the expected count or non-emptiness before iterating. |

KETEST003 appears only with `-p:KhaozTestHealthMode=report`, never gates normal builds and never enters
the baseline. A review of all 474 findings in this repository found about a third real, because loops over local
tables and test-owned case helpers look possibly empty to a syntactic check. Review its candidates
instead of treating them as enforced debt.

Both ratchets use `AnalyzerPathPolicy` for baseline-relative paths and the generated and vendored
file exclusions listed above. Both callers normalize baseline-root separators before calling the policy.
The policy matches the supplied root plus `/` against the source path without case, then collapses
`.` and `..` only inside the extracted relative path. It refuses a `..` with no preceding segment
and applies exclusions to the collapsed key. Other segments, including empty segments from repeated
or trailing separators, keep their existing spelling. Root spelling and prefix matching stay unchanged.
Linked files resolve to their real repository path, so a baseline or `exempt` line names the file where
it lives. The baseline grammar is `<count> <rule> <path>` or
`exempt <rule> <path>`, with paths relative to its directory and a `#` reason line above each exemption:

```text
2 KETEST001 MyGame.Tests/ValidationTests.cs
1 KETEST002 MyGame.Tests/LegacyStructureTests.cs
# Disclosure wording is the contract for this test file. Approved by the repo owner.
exempt KETEST001 MyGame.Tests/DisclosureTests.cs
```

KETEST001 and KETEST002 have separate per-file counts, with zero allowed for an unlisted pair.
Exemptions and any baseline growth require owner approval. The exception path for a legitimate
wording or structural-reflection contract is an approved exemption, never a suppression. An exemption
wins over a numeric entry for the same rule and path. KETEST003 is never baselined.

`scripts/check-test-health.sh` supports three modes, each building a non-incremental Release report of
`KhaozEngine.slnx` and of every tracked xunit project outside it. The script finds those projects with
`git ls-files`, so a test project kept out of the solution is still reported and keeps its allowance:

- `--init` records initial KETEST001 and KETEST002 counts. It refuses an existing numeric baseline and
  preserves adoption-time comments and exemptions. Initial adoption needs owner approval.
- `--preview` prints reductions and growth without writing the baseline.
- `--update` lowers counts or drops resolved entries, preserves exemptions and never raises counts.
  Preview and update return a failure for unapproved growth.

`TESTHEALTH_BUILD_LOG=<file>` makes the script parse that log instead of building. A supplied log with no
KETEST001 or KETEST002 findings over a baseline that still has numeric entries is refused with exit 2,
because an incremental or failed build prints nothing for up-to-date projects and would wipe the
baseline. The script's own build is non-incremental and must succeed, so an empty result from it is
trusted and `--update` drops the last entries.

Ratchet down through the shared build queue:

```sh
build-slot --heavy --label testhealth-update -- sh scripts/check-test-health.sh --update
```

Report mode emits non-exempt findings as warnings without subtracting baseline allowances, including
KETEST003 candidates. To review them without promoting report warnings to errors:

```sh
build-slot --heavy --label testhealth-report -- dotnet build KhaozEngine.slnx -c Release --no-incremental -p:KhaozTestHealthMode=report -p:TreatWarningsAsErrors=false
```

A `.testhealth-baseline` change forces a FULL selective CI run. Tests marked
`[Trait("Category", "Diagnostic")]` report instead of gating, so default CI excludes them with
`Category!=LiveSocket&Category!=Diagnostic`. Run them on demand through the `cross-platform-gpu`
workflow's `renderTestFilter` dispatch input set to `Category=Diagnostic`.

## Subnormal float constants

KEFP001 flags source-visible subnormal `float` constants that can reach the JIT. The JIT folds a float constant through the
floating point controls of the thread that compiles the method, and that machine code then serves every
thread. An optimized compile on a thread with flush to zero set, such as an audio or graphics library thread
that calls into the game, turns a subnormal constant into zero for the life of the process. Probes on macOS
arm64, Linux arm64 and Linux x64 under Rosetta showed it for `AggressiveOptimization`, on-stack replacement
and tiered compilation off. Issue #1832 holds the evidence.

The rule flags:

- A call or method group that resolves to `MathF.BitDecrement`, `MathF.BitIncrement`, `float.BitDecrement`
  or `float.BitIncrement`, including `T.BitDecrement` on a type parameter constrained to
  `IFloatingPointIeee754<float>`. The zero branch of each holds `float.Epsilon`.
  A step with a `dynamic` argument reports at the call when the compiler identifies the BCL step as a
  target or candidate.
- A `BitDecrement` or `BitIncrement` call or method group, or an `Epsilon` reference, on an
  `IFloatingPointIeee754<T>` type parameter. These report where written, even in an unused generic or one
  instantiated only with `double`, because a `float` specialization can expose the subnormal constant
  `float.Epsilon`. An `Epsilon` reference through an `IFloatingPointIeee754<float>` constraint also reports.
  Step a concrete type with `FloatUlp.Down` or `FloatUlp.Up` for `float`, or `Math.BitDecrement` or
  `Math.BitIncrement` for `double`. Read a needed subnormal from a mutable static the JIT cannot fold.
- Any `float` expression whose compile-time constant is subnormal: a literal, `float.Epsilon`, a `const`, a
  folded expression of normal operands, a cast, and a default parameter value at each call that omits it. The
  compiler emits the outermost constant, so `(double)float.Epsilon` is a normal double and stays allowed.
- `BitConverter.Int32BitsToSingle`, `BitConverter.UInt32BitsToSingle`, `Unsafe.BitCast<_, float>` and
  `Unsafe.As<_, float>` of constant bits that are a subnormal float.
- A `static readonly` integer field with a constant initializer whose value is reinterpreted as a subnormal
  float by one of those calls. The analysis applies fixed-width integral and enum conversions before
  classifying the bits and follows conditional, switch and coalesce branches. The JIT folds a static readonly
  field once its class is initialized. A `static readonly float` holding a subnormal is flagged at its constant.
- A `FloatUlp.Down` or `FloatUlp.Up` call that can fold to a subnormal result when the argument is a
  compile-time constant or a recognized constant bit reinterpretation. Both signs of zero take the helper's
  protected path and are allowed.

Concrete double steps stay allowed. The `-double.Epsilon` literal kept its bits under every control and
compile kind in the probes, so `Math.BitDecrement` and `double.BitIncrement` are not flagged.

The fix for a step is `FloatUlp.Down` or `FloatUlp.Up`. An engine project compiles that internal helper in by
setting `KhaozFloatUlp` to `true` (`build/FloatUlp.targets`). It steps in the integer domain without branches
and reads the results for a zero argument, both subnormal, from mutable statics the JIT cannot fold. Its bits match
the BCL steps for every input. It needs no suppression. Its statics stay mutable on purpose: making them
`readonly`, as an IDE suggests, lets the JIT fold them, and this rule reports that.

What the rule cannot see:

- Subnormal values the JIT folds after inlining from operands the compiler never saw as one constant.
  `FloatUlp` reads the result for a zero argument of either sign from a static, so a zero the JIT learns only
  after inlining stays exact. A step that turns the smallest normal into a subnormal, or steps a subnormal,
  still folds when the JIT learns the argument after inlining.
- Folded arithmetic under a directed rounding mode, for `float` and `double`, and `double` folds with a
  subnormal operand or result under a flush mode. No constant rule can detect those.
- Bits read as a float from constant `ReadOnlySpan<byte>` data, through a pointer cast, or from a field set in
  a static constructor. It does not follow a readonly bit field through a local or another readonly field
  into the reinterpretation. A conversion chain that leaves the integral and enum domain, such as
  `(uint)(double)Bits`, stays unknown. The analyzer does not evaluate arithmetic on readonly bit fields
  before reinterpretation. Native-sized integral conversions through `nint` or `nuint` are not evaluated.
- A step or `Epsilon` reached by name without a bound symbol, such as through reflection, `UnsafeAccessor` or
  emitted IL.
- Generated code and hidden source regions that Roslyn excludes from analysis.
- Constants inside BCL or third-party code, such as Bepu, that the JIT inlines into the calling method.
- A generic defined in another assembly, including the BCL, that steps or reads `Epsilon` on its type parameter
  and is instantiated with `float` here.
- Calls to `MathF`, `BitConverter` or `Unsafe` on a target whose core library, the assembly that defines
  `System.Single`, does not define that class, such as `Unsafe` on .NET Standard 2.0. The .NET 10 reference
  assemblies define all three there. A same-named class declared in a compilation that is not itself the
  core library is never taken for the BCL one.

### Scope and consumer adoption

The analyzer reaches consumers through all four umbrellas, and consumers build with warnings as errors, so
KEFP001 reports nothing unless the compilation sets the MSBuild property `KhaozSubnormalFloatRule`:

| Value | Checks |
|-------|--------|
| `on` | Every compilation that does not reference xunit. A test compares against the BCL steps on purpose. |
| `all` | Every compilation, test projects included. |
| Unset or any other value | Nothing. |

The engine sets `on` for every project in its `Directory.Build.props`, so KEFP001 is an error in every
engine compilation that does not reference xunit. Its two floating point test assemblies set `all`, because they
run engine code, frozen reference copies and test helpers under changed controls. A consumer opts in by setting the property in its own
`Directory.Build.props`, for example
`<KhaozSubnormalFloatRule>on</KhaozSubnormalFloatRule>`. The package's buildTransitive props hands the value
to the analyzer. `FloatUlp` is internal to each engine assembly, so a consumer that opts in copies the shape of
`build/FloatUlp.cs` into its own code.
