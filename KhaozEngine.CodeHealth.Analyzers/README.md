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
| KEB32002 | Error | A kernel performs a nonconstant numeric or enum conversion from or to float, double or decimal. Move it into the arithmetic home. |
| KEB32003 | Error | A kernel invokes Math or MathF with floating parameters, or invokes a Single, Double, BitConverter, System.Numerics or System.Runtime.Intrinsics member, including operators. Call the arithmetic home instead. |
| KEB32004 | Error | A kernel reaches an unmarked method or computed accessor, creates a delegate, or uses virtual, abstract, interface or delegate dispatch. Use a statically bound non-virtual kernel or arithmetic-home member. Storage indexers are the deliberate dispatch exception. |
| KEB32005 | Error | A kernel contains an operation outside the explicit allow-list, a copy without identical static element types, or an explicit-layout or FieldOffset field reference. The message names its IOperation kind. Use a permitted operation or move the work into the arithmetic home. |

File-size line count is `wc -l` parity: the number of newline characters. Exclusions match the script:
`obj/`, `bin/`, `vendor/` path segments and `.Designer.cs`, `.g.cs`, `.generated.cs`,
`.AssemblyInfo.cs` suffixes, plus anything Roslyn marks as generated code.

Both file-size messages carry their own remediation inline rather than only in the descriptor description,
because MSBuild prints the message and not the description: guidance an agent cannot see is guidance
that does not exist. The remediation routes a legitimate case to "ask the repo owner", never to a
split that exists only to silence the error.

## Binary32 kernel attribute contract

`Binary32KernelAttribute` opts a method, constructor, accessor, local function or lambda into the
inventory rules. An attribute on a containing method or type applies to its nested kernel members.
Field and auto-property initializers of a marked type are checked too, including static initializers.
The same applies to initializers of a type with any marked instance or static constructor, including
a marked primary constructor. The arithmetic-home exemption still takes precedence.
The attribute is recognized by simple type name in any namespace, including shared attribute
assemblies. The analyzer does not supply the attributes. A type carrying
`Binary32ArithmeticAttribute` is the hand-reviewed arithmetic home. Its members and nested types
are exempt from all five rules, and kernels may call their statically bound non-virtual members.
Kernel membership is cached per compilation. Compilations without a `Binary32KernelAttribute` type register no operation or syntax
actions, even if an arithmetic-home attribute exists.

The inventory fails closed. Every executable `IOperation` kind in a kernel body is checked,
including compiler-generated implicit operations. Anything outside the following allow-list gets
KEB32005. Attribute metadata is outside the executable body. Generated source is analyzed and its
diagnostics are reported, including `.g.cs` files.

- Literals, default values, local, parameter, ordinary field, instance and array-element references.
  References to any field of an explicit-layout type, or any field carrying `FieldOffset`, get
  KEB32005 because they can reinterpret storage. Sequential struct fields and struct copies are legal.
- Simple assignments, integral compound assignments and integral increment or decrement.
- Integral and bool binary or unary operators, and selection with a bool condition (`c ? a : b`).
- Identity, integral and compile-time constant conversions. Existing storage exceptions allow
  reference, boxing, unboxing, same-type nullable lifting or unwrapping, and the trusted implicit
  array, span and memory storage views. Implicit span views require identical element types:
  `T[]` to `Span<T>` or `ReadOnlySpan<T>`, and `Span<T>` to `ReadOnlySpan<T>`, for any T.
  The explicit-cast spelling of these same-element views stays legal. Covariant views with
  differing element types get KEB32005. Nonconstant numeric and enum conversions involving a
  floating type get KEB32002.
- Statically bound non-virtual calls and computed property accesses to kernel or home members.
- Object and exception construction, fields, auto properties, and array, span and
  `IReadOnlyList<T>` indexers. The listed storage indexers may use interface dispatch.
- The trusted System array, span and memory APIs `AsSpan`, `Slice`, `CopyTo`, `TryCopyTo`, `Clear`,
  `Fill` and `Overlaps`, plus `object.ReferenceEquals`, `ArgumentNullException.ThrowIfNull` and
  integer Math overloads. Copy APIs require identical source and destination element types proven
  from their static array, span or memory types. Widening copies and erased `Array` types get
  KEB32005. Unrelated methods with those names are checked.
- Blocks, if, for, while, do, return, break, continue, throw, local declarations, expression
  statements and their structural body, argument and initializer nodes. Empty statements are legal.
- Local functions and lambdas, whose bodies are analyzed as kernel members. Creating or invoking
  a delegate remains prohibited even when its target body is checked.
- Foreach over arrays, `Span<T>` and `ReadOnlySpan<T>` with an identity element conversion only.

Computed properties must mark the accessor actually reached. Integral writes and updates through a
ref-return property reach its getter only. Virtual, abstract, interface and delegate calls, including
calls to marked declarations, get KEB32004. Ordinary auto properties must also be non-virtual.

Patterns, switch expressions and statements, collection expressions and spreads, other enumeration,
coalesce, coalesce-assignment, tuples, deconstruction, dynamic operations, goto, unsafe bodies,
pointers and stackalloc are outside the allow-list. Reinterpretation APIs such as `Unsafe` and
`MemoryMarshal` break the closed call graph. Diagnostic messages carry their own rule and remediation.

The operation order and device-exactness obligations are defined in the
[equipment coverage kernel contract](../docs/design/EQUIPMENT-COVERAGE-KERNEL-CONTRACT-2026-10-18.md).

## Binary32 threat model and known limits

The analyzer guards engine-owned reference code against accidental drift from the binary32 inventory,
not against deliberately adversarial code. The 144-hash test and Part C's native bit-equality tests
are the backstop for reference outputs and device agreement.

Known limits follow from the trusted exceptions and attribute contract:

- Object construction, auto properties and `IReadOnlyList<T>` indexers remain permitted. A
  deliberately crafted unmarked constructor or custom indexer can compute floating results outside
  the inventory. Auto properties with deliberately overlapping explicit-layout backing fields can
  reinterpret values through their trusted accessors.
- The arithmetic home and already stored inputs are trusted. Deliberate work hidden in an unmarked
  type's initialization or performed before entering a kernel is outside this check.
- Attribute identity uses simple type names. Deliberately forged kernel or arithmetic-home
  attributes can impersonate those marks. Storage and guard API identity uses System namespace,
  type and method names, so deliberately forged System types can impersonate those exceptions.

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
