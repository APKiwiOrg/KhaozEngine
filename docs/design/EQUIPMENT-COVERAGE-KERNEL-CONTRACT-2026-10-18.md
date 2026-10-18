# Equipment coverage kernel contract

Status: implementation contract for Part C. Reference snapshot: `505337052`.
Owner rulings: NC2, B16, B19, B25, B27, B30, B31, B32 and B34 to B38.
B38 supersedes every earlier subnormal-rejection rule.

All reference source filenames below are under `KhaozEngine.Render3D/Coverage/`.
A citation names a file and member. The excerpts pin the operation graph, not a shader language or
buffer layout. Part C may change dispatch layout only while preserving these values and decisions.

## 1. Authority

The C# reference is the executable contract. On Vulkan, Metal and Direct3D11 every accepted coverage
output word must equal the reference word bit for bit on every fixture. The device tolerance is zero.
A mismatch fails. Do not loosen a comparison, omit a fixture or regenerate an expectation to pass.

The `MarginFraction x Clearance` reserve is margin against a driver that breaks its precision rules.
It is not a device tolerance. Construction uses the reserve and certification uses the approved
clearance, as specified in the amendment R spec, Part B and Part C.

The numerical authority is `CoverageReferenceArithmetic.cs`, `CoverageReferenceArithmetic`, with the
composite graphs in section 4. B34 and B35's fail-closed `Binary32Kernel` analyzer guards accidental
reference drift. It does not prove a GPU compiler or driver conforms. Native bit comparisons do that.

## 2. Inputs

Upload the exact CPU-produced binary32 words and integers below. Do not recompute cold data or CPU
uniforms in a device kernel. Use the reference's scalar IEEE expression order for numerical
preparation. Vector and matrix structs carry fields. Their vector arithmetic, transform, dot,
normalization and matrix-product operators are not substitutes for the pinned numerical graphs.
First device reads apply section 8, even when a field is subsequently copied rather than calculated.

| Input | Producing file and member | Required contents |
| --- | --- | --- |
| Command order and admission | `CoverageReferenceFrames.cs`, `CoverageReferenceFrames.TryPrepare`, calling `CoverageFrameAdmission.cs`, `CoverageFrameAdmission.TryBegin`, `TryActor` and `TryCommand` | `Order` maps ascending command-ID slots to submitted commands. `RegionCommands` maps regions to slots or -1. Preserve identity, capacity, disjoint-region, finite-cap, conditioning and frame checks. |
| Command frames | `CoverageReferenceFrames.cs`, `CoverageReferenceFrames.Derive`, `Round` and `SpectralUpper` | `HostToCoverage`, `HostToActualSheet`, `ActorLinear`, `Recess`, `ZScale`, `CapMargin`, `ClearanceCoverage`, `ActualClearanceCoverage`, `CorrectionRate` and the original command index. Compose `CoverageAffine` in binary64, round each stored coefficient once to binary32, then derive scalar uniforms in the source order below. |
| Command scalars | `CoverageCommandPose.cs`, `CoverageCommandPose` | `MinZ`, `MaxZ`, `CollarWidth`, `Clearance`, `MaxWorldRecession`, `TransitionWeight`, IDs and region index. Admission remains CPU work. The device uses the admitted binary32 fields. |
| Covered-actor palette | `CoverageReferenceSkinning.cs`, `CoverageReferenceSkinning.Compose` | CPU `inverseBind * jointWorld` with the scalar, left-associated four-product sums below. B31 requires this routine in both staging and oracle. Do not use `SkinningMath.Compose`. Composition is an input producer, not a coverage kernel operation. |
| Skin indices and source attributes | `CoverageReferenceSkinning.cs`, `CoverageReferenceSkin` constructor and `CoverageReferenceSkinning.TrySkin` | Four cold-validated integer palette indices per source vertex, plus source position, normal, color, UV, tangent and binary32 bone weights. No dynamic float-to-int bone-index conversion in a kernel. |
| Support | `CoverageReferenceSupport.cs`, `CoverageReferenceSupport` constructor and `VisitCells` | Binary32 `Positions`, source-sorted `SourceIndices` and `Triangles`, ordered `BoundarySourceVertices`, `MaxSlope`, `GridMin`, `GridMax`, `CellOffsets`, `CellTriangles`. The grid is 16 by 16. `MaxSlope` uses `Cross(Sub3(b,a),Sub3(c,a))` and the home to compute `sqrt(nx*nx + ny*ny)/abs(nz)`. Invalid denominators or candidates produce positive infinity for later refusal. |
| Position and attribute recipes | `CoverageReferenceTopology.cs`, `CoverageReferenceTopology` constructor | `Recipes`, source aliases, source/refined triangles, indices, refined records, `GroupWeights`, `VertexWeights`, `EdgeParameters`, `GroupRepresentatives`. Float weights are the exact binary32 equivalents of integer recipe weights. For an edge choose A/B with numerator WeightB when WeightC is zero, A/C with WeightC when WeightB is zero, otherwise B/C with WeightC. Compute `(a > b ? 12 - numerator : numerator) / 12f` once on the CPU. |
| Gather rows | `CoverageReferenceTopology.cs`, `CoverageReferenceTopology` constructor, `VisitCore` and `Allocate` | Prefix-offset rows `CoreFaceOffsets/CoreFaces`, `VertexFaceOffsets/VertexCorners`, `ShadingCornerOffsets/ShadingCorners`. Core rows have unique incident core-face indices in triangle order. Corner rows contain every matching flattened corner in increasing corner order, including multiplicity. Representatives are the first refined vertex in each position group. |

These CPU members contain arithmetic outside the device inventory. Their results, not their
execution, cross the kernel boundary. `CoverageReferenceFrames.TryPrepare` uses `UniformArithmetic`
separately from the construction trace. Scalar palette and uniform preparation must preserve these
bits on each CPU platform. Cross-CPU execution evidence remains a Part C integration obligation.

### CPU frame preparation

The following input-producing members distinguish binary64 composition and round-once uploads from
device math. The matrix convention is row vectors. These do not become GPU frame-composition kernels.

**`CoverageReferenceFrames.Derive`** from `CoverageReferenceFrames.cs`.

```csharp
static bool Derive(CoverageReferenceArithmetic f, in CoverageAffine actor, in CoverageAffine equipment, in CoverageAffine coverage, in CoverageCommandPose command,
    int commandIndex, out CoverageReferenceFrame frame)
{
    frame = default;
    if (!actor.TryInverse(out var actorInverse) || !coverage.TryInverse(out var coverageInverse) || !equipment.TryInverse(out var equipmentInverse)
        || !CoverageAffine.TryFrom(command.Support.SourceToCoverage, out var projection)) return false;
    Matrix4x4 h = Round(CoverageAffine.Multiply(actor, coverageInverse));
    CoverageAffine actualInverse = CoverageAffine.Multiply(equipmentInverse, projection);
    Matrix4x4 j = Round(CoverageAffine.Multiply(CoverageAffine.Multiply(actor, equipmentInverse), projection));
    Matrix4x4 inverseCoverage = Round(coverageInverse), inverseActual = Round(actualInverse), linear = Round(actor.WithTranslation(default));
    CoverageVector direction = actorInverse.ApplyLinear(coverage.Row(2));
    Vector3 recess = new((float)direction.X, (float)direction.Y, (float)direction.Z);
    Vector3 z = new(command.CoverageWorld.M31, command.CoverageWorld.M32, command.CoverageWorld.M33);
    float zScale = MathF.Sqrt(CoverageReferenceMath.Dot3(f, z, z));
    Vector3 column = new(inverseCoverage.M13, inverseCoverage.M23, inverseCoverage.M33);
    float capMargin = command.Clearance * MathF.Sqrt(CoverageReferenceMath.Dot3(f, column, column));
    float slope = command.Support.ReferenceData.MaxSlope;
    float delta = command.Clearance * .25f;
    float reserved = command.Clearance + delta;
    float steepness = MathF.Sqrt(1 + (slope * slope));
    float clearance = (reserved * steepness) * SpectralUpper(f, inverseCoverage);
    float actualClearance = (reserved * steepness) * SpectralUpper(f, inverseActual);
    Vector3 d = CoverageReferenceMath.Linear(f, recess, j);
    float correctionRate = d.Z - (slope * MathF.Sqrt((d.X * d.X) + (d.Y * d.Y)));
    if (!Finite(h) || !Finite(j) || !Finite(linear) || !CoverageRefinement.Finite(recess) || !float.IsFinite(zScale)
        || !float.IsFinite(capMargin) || !float.IsFinite(clearance) || !float.IsFinite(actualClearance)
        || !float.IsFinite(correctionRate) || !(correctionRate > 0)) return false;
    frame = new(commandIndex, h, j, linear, recess, zScale, capMargin, clearance, actualClearance, correctionRate);
    return true;
}
```

**`CoverageReferenceFrames.SpectralUpper`** from `CoverageReferenceFrames.cs`.

```csharp
static float SpectralUpper(CoverageReferenceArithmetic f, in Matrix4x4 m)
{
    Vector3 r1 = new(m.M11, m.M12, m.M13), r2 = new(m.M21, m.M22, m.M23), r3 = new(m.M31, m.M32, m.M33);
    float g11 = CoverageReferenceMath.Dot3(f, r1, r1), g22 = CoverageReferenceMath.Dot3(f, r2, r2), g33 = CoverageReferenceMath.Dot3(f, r3, r3);
    float g12 = MathF.Abs(CoverageReferenceMath.Dot3(f, r1, r2)), g13 = MathF.Abs(CoverageReferenceMath.Dot3(f, r1, r3)),
        g23 = MathF.Abs(CoverageReferenceMath.Dot3(f, r2, r3));
    return MathF.Sqrt(MathF.Max((g11 + g12) + g13, MathF.Max((g12 + g22) + g23, (g13 + g23) + g33)));
}
```

**`CoverageReferenceFrames.Round`** from `CoverageReferenceFrames.cs`.

```csharp
static Matrix4x4 Round(in CoverageAffine affine)
{
    CoverageVector a = affine.Row(0), b = affine.Row(1), c = affine.Row(2), t = affine.Translation;
    return new((float)a.X, (float)a.Y, (float)a.Z, 0, (float)b.X, (float)b.Y, (float)b.Z, 0,
        (float)c.X, (float)c.Y, (float)c.Z, 0, (float)t.X, (float)t.Y, (float)t.Z, 1);
}
```

### CPU palette preparation

**`CoverageReferenceSkinning.Compose`** from `CoverageReferenceSkinning.cs`.

```csharp
internal static Matrix4x4 Compose(in Matrix4x4 jointWorld, in Matrix4x4 inverseBind)
{
    var j = jointWorld; var b = inverseBind;
    return new(
        (((b.M11 * j.M11) + (b.M12 * j.M21)) + (b.M13 * j.M31)) + (b.M14 * j.M41),
        (((b.M11 * j.M12) + (b.M12 * j.M22)) + (b.M13 * j.M32)) + (b.M14 * j.M42),
        (((b.M11 * j.M13) + (b.M12 * j.M23)) + (b.M13 * j.M33)) + (b.M14 * j.M43),
        (((b.M11 * j.M14) + (b.M12 * j.M24)) + (b.M13 * j.M34)) + (b.M14 * j.M44),
        (((b.M21 * j.M11) + (b.M22 * j.M21)) + (b.M23 * j.M31)) + (b.M24 * j.M41),
        (((b.M21 * j.M12) + (b.M22 * j.M22)) + (b.M23 * j.M32)) + (b.M24 * j.M42),
        (((b.M21 * j.M13) + (b.M22 * j.M23)) + (b.M23 * j.M33)) + (b.M24 * j.M43),
        (((b.M21 * j.M14) + (b.M22 * j.M24)) + (b.M23 * j.M34)) + (b.M24 * j.M44),
        (((b.M31 * j.M11) + (b.M32 * j.M21)) + (b.M33 * j.M31)) + (b.M34 * j.M41),
        (((b.M31 * j.M12) + (b.M32 * j.M22)) + (b.M33 * j.M32)) + (b.M34 * j.M42),
        (((b.M31 * j.M13) + (b.M32 * j.M23)) + (b.M33 * j.M33)) + (b.M34 * j.M43),
        (((b.M31 * j.M14) + (b.M32 * j.M24)) + (b.M33 * j.M34)) + (b.M34 * j.M44),
        (((b.M41 * j.M11) + (b.M42 * j.M21)) + (b.M43 * j.M31)) + (b.M44 * j.M41),
        (((b.M41 * j.M12) + (b.M42 * j.M22)) + (b.M43 * j.M32)) + (b.M44 * j.M42),
        (((b.M41 * j.M13) + (b.M42 * j.M23)) + (b.M43 * j.M33)) + (b.M44 * j.M43),
        (((b.M41 * j.M14) + (b.M42 * j.M24)) + (b.M43 * j.M34)) + (b.M44 * j.M44));
}
```

## 3. Operation inventory

Let `F(x)` be section 8's signed-zero flush and `RN32` round to nearest binary32 with ties to even.
Each scalar arithmetic row flushes every operand, performs its specified operation once, then flushes
its result. Predicates retain an exact sign separately. The `f` calls in section 4 are these rows.
A composite call is not permission to fuse its inner operations.

| `CoverageReferenceArithmetic.cs` member or inventory operation | Exact semantics | Kernel form |
| --- | --- | --- |
| `CoverageReferenceArithmetic.Add` | `F(RN32(F(a) + F(b)))` | Correctly rounded scalar add, no contraction. |
| `CoverageReferenceArithmetic.Sub` | `F(RN32(F(a) - F(b)))` | Correctly rounded scalar subtract. |
| `CoverageReferenceArithmetic.Mul` | `F(RN32(F(a) * F(b)))` | Correctly rounded scalar multiply. |
| `CoverageReferenceArithmetic.Div` | `F(RN32(F(a) / F(b)))` | Corrected divide from section 6, then flush. Preserve signed zero, infinity and invalid-result decisions. |
| `CoverageReferenceArithmetic.Sqrt` | `F(RN32(sqrt(F(a))))` | Corrected square root from section 6, then flush. `sqrt(-0)` is -0. |
| `CoverageReferenceArithmetic.Abs` | Clear the sign bit of `F(a)`, then flush. | Integer mask `bits & 0x7fffffff`. |
| `CoverageReferenceArithmetic.Neg` | Toggle the sign bit of `F(a)`, then flush. | Integer XOR `bits ^ 0x80000000`. |
| `CoverageReferenceArithmetic.Min` | .NET `MathF.Min` on flushed operands, then flush. At zero ties select -0 if either operand is -0. | Compare and select, with sign-bit OR for a zero tie. Never native `min`, `max`, `fmin` or `fmax`. |
| `CoverageReferenceArithmetic.Max` | .NET `MathF.Max` on flushed operands, then flush. At zero ties select +0 if either operand is +0. | Compare and select, with sign-bit AND for a zero tie. Never native `min`, `max`, `fmin` or `fmax`. |
| `CoverageReferenceArithmetic.Clamp` | Flush `x`, `lo`, `hi`, then `F(x < lo ? lo : (x > hi ? hi : x))`, for admitted ordered bounds. | Two comparisons and selects. Keep -0 when `lo` is +0. Do not implement through min/max. |
| `CoverageReferenceArithmetic.Less` | IEEE `F(a) < F(b)`. | Ordered scalar comparison. |
| `CoverageReferenceArithmetic.LessOrEqual` | IEEE `F(a) <= F(b)`. | Ordered scalar comparison. |
| `CoverageReferenceArithmetic.Greater` | IEEE `F(a) > F(b)`. | Ordered scalar comparison. |
| `CoverageReferenceArithmetic.GreaterOrEqual` | IEEE `F(a) >= F(b)`. | Ordered scalar comparison. |
| `CoverageReferenceArithmetic.Equal` | IEEE `F(a) == F(b)`, including -0 equal to +0. False for a NaN operand. | Ordered equality. |
| `CoverageReferenceArithmetic.NotEqual` | C# `F(a) != F(b)`, including -0 equal to +0. True for a NaN operand. | Negation of ordered equality, not an ordered-only inequality instruction. |
| `CoverageReferenceArithmetic.IsFinite(float)` | Flush the local value, then `(bits & 0x7f800000) != 0x7f800000`. | Exponent-field test. Never `isnan` or `isinf`. |
| `CoverageReferenceArithmetic.IsFinite(ref float)` | The same test, also replacing the referenced field with its flushed value. | Flush-and-store followed by exponent-field test. |
| `CoverageReferenceArithmetic.Edge` | Exact sign and `F(RN32(exact determinant))` on flushed XY inputs, section 7. | Exact predicate, not a rounded multiply-subtract sign. |
| `CoverageReferenceArithmetic.Dot` | Exact sign and `F(RN32(exact difference-dot))` on flushed XY inputs, section 7. | Exact predicate, not `Dot2`. |
| `CoverageReferenceArithmetic.CrossingParameter` | Flush input XY, obtain B30 scaled exact sides, flush both sides, return `Div(x, Sub(x, y))`. Invalid or non-opposite sides return NaN. | Section 7 normalization followed by inventory subtract/divide. Caller clamps to [0,1]. |
| `CoverageReferenceArithmetic.Read` | `F(value)`. | Uploaded binary32 read or explicit field store. |
| Binary32 store | Preserve the result word without extra rounding, canonicalization or a wider intermediate. Uploaded values first pass `Read` or the equivalent flush. | Scalar word store or bit-preserving copy. |
| Select | Select the named operand word by the same boolean condition. Selection does not perform arithmetic on the unselected word. | Branch or bit-preserving select. |
| `CoverageReferenceArithmetic.PredicatePoint` | Flush X and Y, copy Z. The predicates only consume XY. | Component reads, two flushes and a copy. Z is flushed when a numerical operation reads it. |
| `CoverageReferenceArithmetic.Flush` | Section 8's bit threshold and sign preservation. | Integer magnitude comparison and select. |
| `CoverageReferenceArithmetic.Reset` | Clear count, first site, phase and element, set slot and first slot to -1. | Host diagnostic reset, no device numerical output. |
| `CoverageReferenceArithmetic.At` | Record phase, element and slot for diagnostics. | Diagnostic metadata only. |
| `EventCount`, `FirstSite`, `FirstSlot` | Diagnostic flush count and first original value bits/site/slot. | Optional trace storage. Never a refusal predicate. |

Relational comparisons are false on NaN. Min/max must propagate NaN to the source's nonfinite guard
rather than silently select the finite operand. Infinities used as initial reduction sentinels retain
their sign. Accepted output vertices are finite. Signed-zero equality does not permit normalizing
the stored sign bits. Integer index arithmetic, integer reductions and boolean selection are
unrestricted provided they preserve the bounds, order and attribution in section 9.

Constants are binary32 words, not decimal strings evaluated at device compile time with a different
precision. Uploaded recipe weights are exact integers 0 to 12 converted once by the CPU.

| Literal or role | Binary32 bits |
| --- | --- |
| `0`, `-0`, `-1` | `0x00000000`, `0x80000000`, `0xbf800000` |
| `1`, `2`, `3`, `4` | `0x3f800000`, `0x40000000`, `0x40400000`, `0x40800000` |
| `5`, `6`, `7`, `8` | `0x40a00000`, `0x40c00000`, `0x40e00000`, `0x41000000` |
| `9`, `10`, `11`, `12f` | `0x41100000`, `0x41200000`, `0x41300000`, `0x41400000` |
| `13`, `14`, `15`, `16f` | `0x41500000`, `0x41600000`, `0x41700000`, `0x41800000` |
| `.25f`, CPU reserve fraction | `0x3e800000` |
| `1e-8f`, skinning threshold | `0x322bcc77` |
| `1e-9f`, face-area threshold | `0x3089705f` |
| `1e-12f`, tangent and basis thresholds | `0x2b8cbccc` |
| `SurvivingFractionSquared = 1f / (1L << 50)` | `0x26800000`, exactly `2^-50` |
| Flush threshold `2^-126` | `0x00800000`, inclusive |
| Positive and negative infinity | `0x7f800000`, `0xff800000` |

`CoverageReferenceSupport.cs`, `CoverageReferenceSupport.CellThresholds`, uses the fifteen literal
words 1 through 15. NaN is an invalid intermediate, not an admitted output constant. Its exponent
field must trigger the same guard. No accepted vertex may carry a NaN payload.

## 4. Pinned order

The following member bodies are exact transcriptions from `505337052`. Each individual `f` operation
rounds and flushes separately. Preserve every nesting and left-to-right dependency, including
`x + 0`, `x * 1`, repeated expressions and short-circuit guard order. Do not reassociate, contract,
replace a division with a reciprocal multiplication, normalize by reciprocal square root or apply
an algebraic simplification. A `new Vector` or `new Matrix` here only stores scalar results.

B37 separates host orchestration from numerical phases. Host code owns traversal, sequencing,
argument checks, scratch and test observers. Host orchestration performs no floating-point operation.
It only stores, copies or passes values produced and consumed by numerical kernel members. Kernels reproduce its sequencing
and data flow, including barriers between immutable face results, group gathers and publication.
Cold CPU input preparation in section 2 remains outside this device arithmetic boundary.

The like-for-like clearance/conversion overrides, reverse-group switch and observers are test seams
that Part C omits. In production `clearance` in correction is `ActualClearanceCoverage`, and
`conversion` is +0. Production correction may spell the positive-deficit expression
`deficit / CorrectionRate`. It is bit-identical to the transcribed `(deficit + 0) / CorrectionRate`
at that branch. This is the explicit override-seam exception, not a general simplification license.

### Shared scalar graphs

**`CoverageReferenceMath.Dot3`** from `CoverageReferenceMath.cs`.

```csharp
internal static float Dot3(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => f.Add(f.Add(f.Mul(a.X, b.X), f.Mul(a.Y, b.Y)), f.Mul(a.Z, b.Z));
```

**`CoverageReferenceMath.Dot2`** from `CoverageReferenceMath.cs`.

```csharp
internal static float Dot2(CoverageReferenceArithmetic f, Vector2 a, Vector2 b) => f.Add(f.Mul(a.X, b.X), f.Mul(a.Y, b.Y));
```

**`CoverageReferenceMath.Cross`** from `CoverageReferenceMath.cs`.

```csharp
internal static Vector3 Cross(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => new Vector3(
    f.Sub(f.Mul(a.Y, b.Z), f.Mul(a.Z, b.Y)), f.Sub(f.Mul(a.Z, b.X), f.Mul(a.X, b.Z)), f.Sub(f.Mul(a.X, b.Y), f.Mul(a.Y, b.X)));
```

**`CoverageReferenceMath.Point`** from `CoverageReferenceMath.cs`.

```csharp
internal static Vector3 Point(CoverageReferenceArithmetic f, Vector3 p, in Matrix4x4 matrix) => new Vector3(
    f.Add(f.Add(f.Add(f.Mul(p.X, matrix.M11), f.Mul(p.Y, matrix.M21)), f.Mul(p.Z, matrix.M31)), matrix.M41),
    f.Add(f.Add(f.Add(f.Mul(p.X, matrix.M12), f.Mul(p.Y, matrix.M22)), f.Mul(p.Z, matrix.M32)), matrix.M42),
    f.Add(f.Add(f.Add(f.Mul(p.X, matrix.M13), f.Mul(p.Y, matrix.M23)), f.Mul(p.Z, matrix.M33)), matrix.M43));
```

**`CoverageReferenceMath.Linear`** from `CoverageReferenceMath.cs`.

```csharp
internal static Vector3 Linear(CoverageReferenceArithmetic f, Vector3 p, in Matrix4x4 matrix) => new Vector3(
    f.Add(f.Add(f.Mul(p.X, matrix.M11), f.Mul(p.Y, matrix.M21)), f.Mul(p.Z, matrix.M31)),
    f.Add(f.Add(f.Mul(p.X, matrix.M12), f.Mul(p.Y, matrix.M22)), f.Mul(p.Z, matrix.M32)),
    f.Add(f.Add(f.Mul(p.X, matrix.M13), f.Mul(p.Y, matrix.M23)), f.Mul(p.Z, matrix.M33)));
```

**`CoverageReferenceMath.Sub3`** from `CoverageReferenceMath.cs`.

```csharp
internal static Vector3 Sub3(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => new Vector3(f.Sub(a.X, b.X), f.Sub(a.Y, b.Y), f.Sub(a.Z, b.Z));
```

**`CoverageReferenceMath.Equal3`** from `CoverageReferenceMath.cs`.

```csharp
internal static bool Equal3(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => f.Equal(a.X, b.X) && f.Equal(a.Y, b.Y) && f.Equal(a.Z, b.Z);
```

**`CoverageReferenceMath.IsFinite3`** from `CoverageReferenceMath.cs`.

```csharp
internal static bool IsFinite3(CoverageReferenceArithmetic f, Vector3 v) => f.IsFinite(v.X) && f.IsFinite(v.Y) && f.IsFinite(v.Z);
```

**`CoverageReferenceMath.IsFiniteVertex`** from `CoverageReferenceMath.cs`.

```csharp
internal static bool IsFiniteVertex(CoverageReferenceArithmetic f, ModelVertex v) => IsFinite3(f, v.Position) && IsFinite3(f, v.Normal)
    && f.IsFinite(v.Color.X) && f.IsFinite(v.Color.Y) && f.IsFinite(v.Color.Z) && f.IsFinite(v.Color.W)
    && f.IsFinite(v.Uv.X) && f.IsFinite(v.Uv.Y) && f.IsFinite(v.Tangent.X) && f.IsFinite(v.Tangent.Y)
    && f.IsFinite(v.Tangent.Z) && f.IsFinite(v.Tangent.W);
```

**`CoverageReferenceMath.ReadVertex`** from `CoverageReferenceMath.cs`.

```csharp
internal static bool ReadVertex(CoverageReferenceArithmetic f, ref ModelVertex v) =>
    f.IsFinite(ref v.Position.X) && f.IsFinite(ref v.Position.Y) && f.IsFinite(ref v.Position.Z)
    && f.IsFinite(ref v.Normal.X) && f.IsFinite(ref v.Normal.Y) && f.IsFinite(ref v.Normal.Z)
    && f.IsFinite(ref v.Color.X) && f.IsFinite(ref v.Color.Y) && f.IsFinite(ref v.Color.Z) && f.IsFinite(ref v.Color.W)
    && f.IsFinite(ref v.Uv.X) && f.IsFinite(ref v.Uv.Y) && f.IsFinite(ref v.Tangent.X) && f.IsFinite(ref v.Tangent.Y)
    && f.IsFinite(ref v.Tangent.Z) && f.IsFinite(ref v.Tangent.W);
```

**`CoverageReferenceMath.Height`** from `CoverageReferenceMath.cs`.

```csharp
internal static float Height(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 c, Vector2 p)
{
    if (f.Equal(p.X, a.X) && f.Equal(p.Y, a.Y)) return f.Read(a.Z);
    if (f.Equal(p.X, b.X) && f.Equal(p.Y, b.Y)) return f.Read(b.Z);
    if (f.Equal(p.X, c.X) && f.Equal(p.Y, c.Y)) return f.Read(c.Z);
    Vector3 query = new Vector3(p, 0);
    CoverageReferenceScalar u = f.Edge(b, c, query), v = f.Edge(c, a, query), w = f.Edge(a, b, query);
    if (u.Sign == 0) return EdgeHeight(f, b, c, p);
    if (v.Sign == 0) return EdgeHeight(f, c, a, p);
    if (w.Sign == 0) return EdgeHeight(f, a, b, p);
    return f.Div(f.Add(f.Add(f.Mul(u.Value, a.Z), f.Mul(v.Value, b.Z)), f.Mul(w.Value, c.Z)), f.Edge(a, b, c).Value);
}
```

**`CoverageReferenceMath.EdgeHeight`** from `CoverageReferenceMath.cs`.

```csharp
static float EdgeHeight(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector2 p)
{
    if (f.Greater(a.X, b.X) || f.Equal(a.X, b.X) && (f.Greater(a.Y, b.Y) || f.Equal(a.Y, b.Y) && f.Greater(a.Z, b.Z)))
    { Vector3 temporary = a; a = b; b = temporary; }
    float dx = f.Sub(b.X, a.X), dy = f.Sub(b.Y, a.Y);
    float t = f.GreaterOrEqual(f.Abs(dx), f.Abs(dy)) ? f.Div(f.Sub(p.X, a.X), dx) : f.Div(f.Sub(p.Y, a.Y), dy);
    return f.Add(f.Mul(f.Sub(1, t), a.Z), f.Mul(t, b.Z));
}
```

### Source skinning

**`CoverageReferenceSkinning.TrySkinVertex`** from `CoverageReferenceSkinning.cs`.

```csharp
static bool TrySkinVertex(CoverageReferenceArithmetic f, in SkinnedVertex v, int vertex, int[] indices,
    ReadOnlySpan<Matrix4x4> composed, out ModelVertex posed)
{
    f.At(CoverageExactnessPhase.Skinning, vertex);
    Vector4 w = v.BoneWeights;
    float total = f.Add(f.Add(f.Add(w.X, w.Y), w.Z), w.W);
    Matrix4x4 m = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    if (!f.Less(total, 1e-8f))
    {
        Matrix4x4 a = composed[indices[vertex * 4]], b = composed[indices[vertex * 4 + 1]],
            c = composed[indices[vertex * 4 + 2]], d = composed[indices[vertex * 4 + 3]];
        m = new Matrix4x4(
            f.Add(f.Add(f.Add(f.Mul(a.M11, w.X), f.Mul(b.M11, w.Y)), f.Mul(c.M11, w.Z)), f.Mul(d.M11, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M12, w.X), f.Mul(b.M12, w.Y)), f.Mul(c.M12, w.Z)), f.Mul(d.M12, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M13, w.X), f.Mul(b.M13, w.Y)), f.Mul(c.M13, w.Z)), f.Mul(d.M13, w.W)),
            0,
            f.Add(f.Add(f.Add(f.Mul(a.M21, w.X), f.Mul(b.M21, w.Y)), f.Mul(c.M21, w.Z)), f.Mul(d.M21, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M22, w.X), f.Mul(b.M22, w.Y)), f.Mul(c.M22, w.Z)), f.Mul(d.M22, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M23, w.X), f.Mul(b.M23, w.Y)), f.Mul(c.M23, w.Z)), f.Mul(d.M23, w.W)),
            0,
            f.Add(f.Add(f.Add(f.Mul(a.M31, w.X), f.Mul(b.M31, w.Y)), f.Mul(c.M31, w.Z)), f.Mul(d.M31, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M32, w.X), f.Mul(b.M32, w.Y)), f.Mul(c.M32, w.Z)), f.Mul(d.M32, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M33, w.X), f.Mul(b.M33, w.Y)), f.Mul(c.M33, w.Z)), f.Mul(d.M33, w.W)),
            0,
            f.Add(f.Add(f.Add(f.Mul(a.M41, w.X), f.Mul(b.M41, w.Y)), f.Mul(c.M41, w.Z)), f.Mul(d.M41, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M42, w.X), f.Mul(b.M42, w.Y)), f.Mul(c.M42, w.Z)), f.Mul(d.M42, w.W)),
            f.Add(f.Add(f.Add(f.Mul(a.M43, w.X), f.Mul(b.M43, w.Y)), f.Mul(c.M43, w.Z)), f.Mul(d.M43, w.W)),
            1);
    }
    Vector3 p = CoverageReferenceMath.Point(f, v.Position, m), n = CoverageReferenceMath.Linear(f, v.Normal, m);
    float length = f.Sqrt(CoverageReferenceMath.Dot3(f, n, n));
    n = f.Greater(length, 1e-8f) ? new Vector3(f.Div(n.X, length), f.Div(n.Y, length), f.Div(n.Z, length)) : v.Normal;
    Vector3 t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
    Vector4 tangent = default;
    if (f.Greater(CoverageReferenceMath.Dot3(f, t, t), 1e-12f))
    {
        Vector3 transformed = CoverageReferenceMath.Linear(f, t, m);
        float tl = f.Sqrt(CoverageReferenceMath.Dot3(f, transformed, transformed));
        transformed = f.Greater(tl, 1e-8f) ? new Vector3(f.Div(transformed.X, tl), f.Div(transformed.Y, tl), f.Div(transformed.Z, tl)) : t;
        tangent = new Vector4(transformed, v.Tangent.W);
    }
    posed = new ModelVertex(p, n, v.Color, v.Uv, tangent);
    return CoverageReferenceMath.ReadVertex(f, ref posed);
}
```

### Refinement and point conformation

**`CoverageReferenceConstructor.ValidateSource`** from `CoverageReferenceConstructor.cs`.

```csharp
static bool ValidateSource(CoverageReferenceArithmetic f, ModelVertex input, int vertex, out ModelVertex result)
{
    f.At(CoverageExactnessPhase.SourceValidation, vertex);
    result = input;
    return CoverageReferenceMath.ReadVertex(f, ref result);
}
```

**`CoverageReferenceConstructor.AliasesEqual`** from `CoverageReferenceConstructor.cs`.

```csharp
static bool AliasesEqual(CoverageReferenceArithmetic f, Vector3 position, Vector3 representative, int vertex)
{
    f.At(CoverageExactnessPhase.SourceValidation, vertex);
    return CoverageReferenceMath.Equal3(f, position, representative);
}
```

**`CoverageReferenceConstructor.Interpolate`** from `CoverageReferenceConstructor.cs`.

```csharp
static bool Interpolate(CoverageReferenceArithmetic f, in CoveragePositionRecipe recipe, in CoverageReferenceWeights weights,
    float edgeParameter, ReadOnlySpan<ModelVertex> posed, int group, out Vector3 position)
{
    f.At(CoverageExactnessPhase.GroupInterpolation, group);
    position = Position(f, recipe, weights, edgeParameter, posed);
    return CoverageReferenceMath.IsFinite3(f, position);
}
```

**`CoverageReferenceConstructor.ValidClearance`** from `CoverageReferenceConstructor.cs`.

```csharp
static bool ValidClearance(CoverageReferenceArithmetic f, float clearance) => f.IsFinite(clearance) && !f.Less(clearance, 0);
```

**`CoverageReferenceConstructor.IsActive`** from `CoverageReferenceConstructor.cs`.

```csharp
static bool IsActive(CoverageReferenceArithmetic f, float strength, int vertex, int slot)
{
    f.At(CoverageExactnessPhase.Conformation, vertex, slot);
    return f.Greater(strength, 0);
}
```

**`CoverageReferenceConstructor.Position`** from `CoverageReferenceConstructor.cs`.

```csharp
static Vector3 Position(CoverageReferenceArithmetic f, in CoveragePositionRecipe recipe, in CoverageReferenceWeights weights, float edgeParameter, ReadOnlySpan<ModelVertex> posed)
{
    if (recipe.WeightA == 12) return posed[recipe.A].Position;
    if (recipe.WeightB == 12) return posed[recipe.B].Position;
    if (recipe.WeightC == 12) return posed[recipe.C].Position;
    if (recipe.WeightC == 0) return Edge(f, recipe.A, recipe.B, edgeParameter, posed);
    if (recipe.WeightB == 0) return Edge(f, recipe.A, recipe.C, edgeParameter, posed);
    if (recipe.WeightA == 0) return Edge(f, recipe.B, recipe.C, edgeParameter, posed);
    Vector3 a = posed[recipe.A].Position, b = posed[recipe.B].Position, c = posed[recipe.C].Position;
    return new Vector3(f.Div(Sum(f, a.X, b.X, c.X, weights.A, weights.B, weights.C), 12f),
        f.Div(Sum(f, a.Y, b.Y, c.Y, weights.A, weights.B, weights.C), 12f),
        f.Div(Sum(f, a.Z, b.Z, c.Z, weights.A, weights.B, weights.C), 12f));
}
```

**`CoverageReferenceConstructor.Edge`** from `CoverageReferenceConstructor.cs`.

```csharp
static Vector3 Edge(CoverageReferenceArithmetic f, int a, int b, float t, ReadOnlySpan<ModelVertex> posed)
{
    if (a > b) { int temporary = a; a = b; b = temporary; }
    Vector3 first = posed[a].Position, second = posed[b].Position;
    return new Vector3(f.Add((f.Mul(first.X, (f.Sub(1, t)))), (f.Mul(second.X, t))), f.Add((f.Mul(first.Y, (f.Sub(1, t)))), (f.Mul(second.Y, t))), f.Add((f.Mul(first.Z, (f.Sub(1, t)))), (f.Mul(second.Z, t))));
}
```

**`CoverageReferenceConstructor.Sum`** from `CoverageReferenceConstructor.cs`.

```csharp
static float Sum(CoverageReferenceArithmetic f, float a, float b, float c, float wa, float wb, float wc) => f.Add((f.Add((f.Mul(a, wa)), (f.Mul(b, wb)))), (f.Mul(c, wc)));
```

**`CoverageReferenceConstructor.Value`** from `CoverageReferenceConstructor.cs`.

```csharp
static float Value(CoverageReferenceArithmetic f, float a, float b, float c, in CoverageReferenceWeights weights) => f.Div(Sum(f, a, b, c, weights.A, weights.B, weights.C), 12f);
```

**`CoverageReferenceConstructor.Magnitude`** from `CoverageReferenceConstructor.cs`.

```csharp
static float Magnitude(CoverageReferenceArithmetic f, float a, float b, float c, in CoverageReferenceWeights weights) =>
    f.Add((f.Add(f.Abs(f.Mul(a, weights.A)), f.Abs(f.Mul(b, weights.B)))), f.Abs(f.Mul(c, weights.C)));
```

**`CoverageReferenceConstructor.Direction`** from `CoverageReferenceConstructor.cs`.

```csharp
static bool Direction(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 c, in CoverageReferenceWeights weights, out Vector3 unit)
{
    unit = default;
    Vector3 sum = new Vector3(Sum(f, a.X, b.X, c.X, weights.A, weights.B, weights.C), Sum(f, a.Y, b.Y, c.Y, weights.A, weights.B, weights.C),
        Sum(f, a.Z, b.Z, c.Z, weights.A, weights.B, weights.C));
    Vector3 magnitude = new Vector3(Magnitude(f, a.X, b.X, c.X, weights), Magnitude(f, a.Y, b.Y, c.Y, weights), Magnitude(f, a.Z, b.Z, c.Z, weights));
    if (!CoverageReferenceMath.IsFinite3(f, sum) || !CoverageReferenceMath.IsFinite3(f, magnitude)) return false;
    if (CoverageReferenceMath.Equal3(f, magnitude, default(Vector3))) { unit = sum; return true; }
    float lengthSquared = CoverageReferenceMath.Dot3(f, sum, sum), magnitudeSquared = CoverageReferenceMath.Dot3(f, magnitude, magnitude);
    if (!f.IsFinite(lengthSquared) || !f.IsFinite(magnitudeSquared)
        || !(f.Greater(lengthSquared, f.Mul(magnitudeSquared, SurvivingFractionSquared)))) return false;
    float length = f.Sqrt(lengthSquared);
    unit = new Vector3(f.Div(sum.X, length), f.Div(sum.Y, length), f.Div(sum.Z, length));
    return CoverageReferenceMath.IsFinite3(f, unit);
}
```

**`CoverageReferenceConstructor.TryVertex`** from `CoverageReferenceConstructor.cs`.

```csharp
static bool TryVertex(CoverageReferenceArithmetic f, int vertex, ReadOnlySpan<ModelVertex> posed,
    CoverageReferenceScratch scratch, out ModelVertex result)
{
    Vector3 normal;
    Vector3 tangentDirection;
    var data = scratch.ReferenceTopology;
    var record = data.Vertices[vertex];
    var weights = data.VertexWeights[vertex];
    f.At(CoverageExactnessPhase.AttributeInterpolation, vertex);
    var triangle = data.SourceTriangles[record.SourceTriangle];
    ModelVertex a = posed[triangle.A], b = posed[triangle.B], c = posed[triangle.C];
    result = default;
    if (!Direction(f, a.Normal, b.Normal, c.Normal, weights, out normal)
        || !Direction(f, new Vector3(a.Tangent.X, a.Tangent.Y, a.Tangent.Z), new Vector3(b.Tangent.X, b.Tangent.Y, b.Tangent.Z),
            new Vector3(c.Tangent.X, c.Tangent.Y, c.Tangent.Z), weights, out tangentDirection)) return false;
    Vector4 tangent = default(Vector4);
    if (!CoverageReferenceMath.Equal3(f, tangentDirection, default(Vector3)))
    {
        float handedness = Sum(f, a.Tangent.W, b.Tangent.W, c.Tangent.W, weights.A, weights.B, weights.C);
        if (!f.IsFinite(handedness)) return false;
        tangent = new Vector4(tangentDirection, f.Less(handedness, 0) ? (float)-1 : 1f);
    }
    result = new ModelVertex(scratch.GroupPositions[record.PositionGroup], normal,
        new Vector4(Value(f, a.Color.X, b.Color.X, c.Color.X, weights), Value(f, a.Color.Y, b.Color.Y, c.Color.Y, weights),
            Value(f, a.Color.Z, b.Color.Z, c.Color.Z, weights), Value(f, a.Color.W, b.Color.W, c.Color.W, weights)),
        new Vector2(Value(f, a.Uv.X, b.Uv.X, c.Uv.X, weights), Value(f, a.Uv.Y, b.Uv.Y, c.Uv.Y, weights)), tangent);
    return CoverageReferenceMath.IsFiniteVertex(f, result);
}
```

**`CoverageReferenceConstructor.Conform`** from `CoverageReferenceConstructor.cs`.

```csharp
static Vector3 Conform(CoverageReferenceArithmetic f, Vector3 position, float strength, bool core, int vertex, int group, int slot,
    ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, ref CoverageTally tally)
{
    float height;
    f.At(CoverageExactnessPhase.Conformation, vertex, slot);
    var frame = scratch.Frames[slot];
    var command = commands[frame.Command];
    var support = command.Support.ReferenceData;
    Vector3 q = CoverageReferenceMath.Point(f, position, frame.HostToCoverage);
    float lower = f.Add(command.MinZ, frame.CapMargin), upper = f.Sub(command.MaxZ, frame.CapMargin);
    if (!CoverageReferenceMath.IsFinite3(f, q) || !f.IsFinite(lower) || !f.IsFinite(upper))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
    if (f.Less(q.Z, lower) || f.Greater(q.Z, upper))
    { tally.Note(CoverageFailureCode.CapContact, slot); return position; }
    float spatial = 1;
    Vector2 xy = new Vector2(q.X, q.Y);
    if (!support.TryHeight(f, xy, out height))
    {
        float distance;
        if (core) { tally.Note(CoverageFailureCode.CoreEscaped, slot); return position; }
        if (!support.TryBoundary(f, xy, out height, out distance))
        { tally.Note(CoverageFailureCode.UnsupportedSupport, slot); return position; }
        if (f.GreaterOrEqual(distance, command.CollarWidth)) return position;
        float u = f.Clamp(f.Sub(1, (f.Div(distance, command.CollarWidth))), 0, 1);
        spatial = f.Mul((f.Mul(u, u)), (f.Sub(3, (f.Mul(2, u)))));
    }
    float target = f.Sub(height, frame.ClearanceCoverage), difference = f.Sub(q.Z, target);
    float request = f.Max(0, difference);
    float full = f.Mul((f.Mul((core ? 1 : strength), spatial)), request);
    float world = f.Mul(full, frame.ZScale);
    if (!f.IsFinite(height) || !f.IsFinite(spatial) || !f.IsFinite(target) || !f.IsFinite(difference)
        || !f.IsFinite(full) || !f.IsFinite(world))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
    if (f.Greater(world, command.MaxWorldRecession))
    { tally.Note(CoverageFailureCode.RecessionExceeded, slot); return position; }
    if (f.Equal(full, 0)) return position;
    Vector3 moved = new Vector3(f.Sub(position.X, (f.Mul(full, frame.Recess.X))), f.Sub(position.Y, (f.Mul(full, frame.Recess.Y))), f.Sub(position.Z, (f.Mul(full, frame.Recess.Z))));
    Vector3 movedQ = CoverageReferenceMath.Point(f, moved, frame.HostToCoverage);
    if (!CoverageReferenceMath.IsFinite3(f, moved) || !CoverageReferenceMath.IsFinite3(f, movedQ))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position; }
    if (f.Less(movedQ.Z, lower) || f.Greater(movedQ.Z, upper))
    { tally.Note(CoverageFailureCode.CapContact, slot); return position; }
    scratch.FullRecessions[group] = full;
    return CoverageReferenceMath.Equal3(f, moved, position) ? position : moved;
}
```

### Support queries and overlay

**`CoverageReferenceSupport.Bounds`** from `CoverageReferenceSupport.cs`.

```csharp
static void Bounds(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 c, out Vector2 min, out Vector2 max)
{
    min = new Vector2(f.Min(a.X, f.Min(b.X, c.X)), f.Min(a.Y, f.Min(b.Y, c.Y)));
    max = new Vector2(f.Max(a.X, f.Max(b.X, c.X)), f.Max(a.Y, f.Max(b.Y, c.Y)));
}
```

**`CoverageReferenceSupport.Cell`** from `CoverageReferenceSupport.cs`.

```csharp
internal static int Cell(CoverageReferenceArithmetic f, float value, float min, float max)
{
    float x = f.Mul(f.Div(f.Sub(value, min), f.Sub(max, min)), 16f);
    int cell = 0;
    foreach (float threshold in CellThresholds) if (f.GreaterOrEqual(x, threshold)) cell++;
    return cell;
}
```

**`CoverageReferenceSupport.TryHeight`** from `CoverageReferenceSupport.cs`.

```csharp
internal bool TryHeight(CoverageReferenceArithmetic f, Vector2 point, out float height)
{
    int ignored1;
    return TryHeight(f, point, out height, out ignored1);
}
```

**`CoverageReferenceSupport.TryHeight`** from `CoverageReferenceSupport.cs`.

```csharp
internal bool TryHeight(CoverageReferenceArithmetic f, Vector2 point, out float height, out int sourceTriangle)
{
    height = 0;
    sourceTriangle = -1;
    if (!f.IsFinite(point.X) || !f.IsFinite(point.Y) || f.Less(point.X, GridMin.X) || f.Greater(point.X, GridMax.X)
        || f.Less(point.Y, GridMin.Y) || f.Greater(point.Y, GridMax.Y)) return false;
    int cell = Cell(f, point.Y, GridMin.Y, GridMax.Y) * GridSize + Cell(f, point.X, GridMin.X, GridMax.X);
    Vector3 p = new Vector3(point, 0);
    for (int i = CellOffsets[cell]; i < CellOffsets[cell + 1]; i++)
    {
        Vector2 min;
        Vector2 max;
        var t = Triangles[CellTriangles[i]];
        Vector3 a = Positions[t.A], b = Positions[t.B], c = Positions[t.C];
        Bounds(f, a, b, c, out min, out max);
        if (f.Less(point.X, min.X) || f.Greater(point.X, max.X) || f.Less(point.Y, min.Y) || f.Greater(point.Y, max.Y)) continue;
        CoverageReferenceScalar area = f.Edge(a, b, c), u = f.Edge(b, c, p),
            v = f.Edge(c, a, p), w = f.Edge(a, b, p);
        if (!f.IsFinite(area.Value) || area.Sign == 0 || !f.IsFinite(u.Value) || !f.IsFinite(v.Value) || !f.IsFinite(w.Value)) return false;
        if (u.Sign * area.Sign < 0 || v.Sign * area.Sign < 0 || w.Sign * area.Sign < 0) continue;
        height = CoverageReferenceMath.Height(f, a, b, c, point);
        if (!f.IsFinite(height)) return false;
        sourceTriangle = SourceIndices[CellTriangles[i]];
        return true;
    }
    return false;
}
```

**`CoverageReferenceSupport.TryBoundary`** from `CoverageReferenceSupport.cs`.

```csharp
internal bool TryBoundary(CoverageReferenceArithmetic f, Vector2 point, out float height, out float distance)
{
    height = distance = 0;
    if (!f.IsFinite(point.X) || !f.IsFinite(point.Y)) return false;
    float best = float.PositiveInfinity;
    for (int edge = 0; edge < BoundaryCount; edge++)
    {
        Vector3 a = Positions[BoundarySourceVertices[edge]], b = Positions[BoundarySourceVertices[(edge + 1) % BoundaryCount]];
        Vector2 delta = new Vector2(f.Sub(b.X, a.X), f.Sub(b.Y, a.Y));
        CoverageReferenceScalar length = f.Dot(a, b, a, b),
            dot = f.Dot(a, new Vector3(point, 0), a, b);
        if (length.Sign <= 0 || !f.IsFinite(length.Value) || !f.IsFinite(dot.Value)) return false;
        float projection = f.Div(dot.Value, length.Value);
        if (!f.IsFinite(projection)) return false;
        float t = f.Clamp(projection, 0, 1);
        Vector2 nearest = new Vector2(f.Add(a.X, (f.Mul(t, delta.X))), f.Add(a.Y, (f.Mul(t, delta.Y))));
        Vector2 remaining = new Vector2(f.Sub(point.X, nearest.X), f.Sub(point.Y, nearest.Y));
        float squared = CoverageReferenceMath.Dot2(f, remaining, remaining);
        float candidate = f.Add(a.Z, (f.Mul(t, (f.Sub(b.Z, a.Z)))));
        if (!f.IsFinite(squared) || !f.IsFinite(candidate)) return false;
        if (f.Less(squared, best)) { best = squared; height = candidate; }
    }
    distance = f.Sqrt(best);
    return f.IsFinite(distance);
}
```

**`CoverageReferenceSupport.OverlayMaximum`** from `CoverageReferenceSupport.cs`.

```csharp
bool OverlayMaximum(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> host, Span<Vector3> sheet,
    Span<CoverageReferenceScalar> hostInSheet, Span<CoverageReferenceScalar> sheetInHost, out float maximum)
{
    maximum = float.NegativeInfinity;
    for (int i = 0; i < 3; i++) if (!CoverageReferenceMath.IsFinite3(f, host[i])) return false;
    CoverageReferenceScalar hostArea = f.Edge(host[0], host[1], host[2]);
    if (hostArea.Sign == 0 || !f.IsFinite(hostArea.Value)) return false;
    for (int t = 0; t < TriangleCount; t++)
    {
        var triangle = Triangles[t];
        sheet[0] = Positions[triangle.A]; sheet[1] = Positions[triangle.B]; sheet[2] = Positions[triangle.C];
        if (!OverlayPair(f, host, sheet, hostArea, hostInSheet, sheetInHost, ref maximum)) return false;
    }
    return true;
}
```

**`CoverageReferenceSupport.OverlayPair`** from `CoverageReferenceSupport.cs`.

```csharp
static bool OverlayPair(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> host, ReadOnlySpan<Vector3> sheet, CoverageReferenceScalar hostArea,
    Span<CoverageReferenceScalar> hostInSheet, Span<CoverageReferenceScalar> sheetInHost, ref float maximum)
{
    for (int i = 0; i < 3; i++) if (!CoverageReferenceMath.IsFinite3(f, sheet[i])) return false;
    CoverageReferenceScalar sheetArea = f.Edge(sheet[0], sheet[1], sheet[2]);
    if (sheetArea.Sign == 0 || !f.IsFinite(sheetArea.Value)) return false;
    for (int i = 0; i < 3; i++)
    {
        bool inside;
        if (!OverlayInside(f, sheet, host[i], sheetArea, hostInSheet.Slice(i * 3, 3), out inside)) return false;
        if (inside && !OverlayAccumulate(f, host, sheet, host[i], ref maximum)) return false;
        if (!OverlayInside(f, host, sheet[i], hostArea, sheetInHost.Slice(i * 3, 3), out inside)) return false;
        if (inside && !OverlayAccumulate(f, host, sheet, sheet[i], ref maximum)) return false;
    }
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            int nextI = (i + 1) % 3, nextJ = (j + 1) % 3;
            CoverageReferenceScalar ca = hostInSheet[i * 3 + j], cb = hostInSheet[nextI * 3 + j],
                ac = sheetInHost[j * 3 + i], ad = sheetInHost[nextJ * 3 + i];
            if (ca.Sign * cb.Sign > 0 || ac.Sign * ad.Sign > 0) continue;
            // Collinear overlap extrema were included as corners. Exact endpoint touches need no division.
            if (ca.Sign == 0 && cb.Sign == 0) continue;
            Vector3 a = host[i], b = host[nextI], p;
            if (ca.Sign == 0) p = a;
            else if (cb.Sign == 0) p = b;
            else
            {
                float quotient = f.CrossingParameter(sheet[j], sheet[nextJ], a, b);
                if (!f.IsFinite(quotient)) return false;
                float u = f.Clamp(quotient, 0, 1);
                p = new Vector3(f.Add(a.X, (f.Mul(u, (f.Sub(b.X, a.X))))), f.Add(a.Y, (f.Mul(u, (f.Sub(b.Y, a.Y))))), f.Add(a.Z, (f.Mul(u, (f.Sub(b.Z, a.Z))))));
            }
            if (!CoverageReferenceMath.IsFinite3(f, p) || !OverlayAccumulate(f, host, sheet, p, ref maximum)) return false;
        }
    return true;
}
```

**`CoverageReferenceSupport.OverlayInside`** from `CoverageReferenceSupport.cs`.

```csharp
static bool OverlayInside(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> triangle, Vector3 p, CoverageReferenceScalar area,
    Span<CoverageReferenceScalar> sides, out bool inside)
{
    inside = true;
    for (int i = 0; i < 3; i++)
    {
        CoverageReferenceScalar side = sides[i] = f.Edge(triangle[i], triangle[(i + 1) % 3], p);
        if (!f.IsFinite(side.Value)) return false;
        if (side.Sign * area.Sign < 0) inside = false;
    }
    return true;
}
```

**`CoverageReferenceSupport.OverlayAccumulate`** from `CoverageReferenceSupport.cs`.

```csharp
static bool OverlayAccumulate(CoverageReferenceArithmetic f, ReadOnlySpan<Vector3> host, ReadOnlySpan<Vector3> sheet, Vector3 p, ref float maximum)
{
    Vector2 xy = new Vector2(p.X, p.Y);
    float h = CoverageReferenceMath.Height(f, host[0], host[1], host[2], xy),
        s = CoverageReferenceMath.Height(f, sheet[0], sheet[1], sheet[2], xy), difference = f.Sub(h, s);
    if (!f.IsFinite(h) || !f.IsFinite(s) || !f.IsFinite(difference)) return false;
    maximum = f.Max(maximum, difference);
    return true;
}
```

### Core correction, full-target validation and transition

**`CoverageReferenceCorrection.PrepareFace`** from `CoverageReferenceCorrection.cs`.

```csharp
static bool PrepareFace(CoverageReferenceArithmetic f, CoverageReferenceTopology data, CoverageReferenceScratch scratch,
    int triangle, int slot, CoverageReferenceSupport support, in CoverageReferenceFrame frame, float clearance, float conversion,
    Span<Vector3> host, ref CoverageTally tally, out float discrepancy)
{
    f.At(CoverageExactnessPhase.FaceCorrection, triangle, slot);
    discrepancy = 0;
    if (!(f.Greater(frame.CorrectionRate, 0)) || !f.IsFinite(frame.CorrectionRate)
        || !f.IsFinite(clearance) || f.Less(clearance, 0) || !f.IsFinite(conversion) || f.Less(conversion, 0))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
    bool valid = true;
    for (int corner = 0; corner < 3; corner++)
    {
        float ignored1;
        float ignored2;
        int vertex = (int)data.Indices[triangle * 3 + corner];
        Vector3 position = scratch.After[vertex].Position;
        Vector3 actual = CoverageReferenceMath.Point(f, position, frame.HostToActualSheet),
            nominal = CoverageReferenceMath.Point(f, position, frame.HostToCoverage);
        if (!CoverageReferenceMath.IsFinite3(f, actual) || !CoverageReferenceMath.IsFinite3(f, nominal))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); valid = false; continue; }
        if (!support.TryHeight(f, new Vector2(actual.X, actual.Y), out ignored1) || !support.TryHeight(f, new Vector2(nominal.X, nominal.Y), out ignored2))
        { tally.Note(CoverageFailureCode.CoreEscaped, slot); valid = false; continue; }
        host[corner] = nominal;
        float x = f.Sub(actual.X, nominal.X), y = f.Sub(actual.Y, nominal.Y), z = f.Sub(actual.Z, nominal.Z);
        float loss = f.Add(f.Abs(z), (f.Mul(support.MaxSlope, f.Sqrt(f.Add((f.Mul(x, x)), (f.Mul(y, y)))))));
        if (!f.IsFinite(loss))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); valid = false; continue; }
        discrepancy = f.Max(discrepancy, loss);
    }
    if (!valid) return false;
    return true;
}
```

**`CoverageReferenceCorrection.FinishFace`** from `CoverageReferenceCorrection.cs`.

```csharp
static bool FinishFace(CoverageReferenceArithmetic f, CoverageReferenceTopology data, int triangle, int slot,
    in CoverageReferenceFrame frame, float clearance, float conversion, float maximum, float discrepancy,
    ref CoverageTally tally, out float correction)
{
    correction = 0;
    float deficit = f.Add((f.Add(maximum, clearance)), discrepancy);
    if (!f.IsFinite(deficit))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
    if (f.LessOrEqual(deficit, 0)) return true;
    correction = f.Div((f.Add(deficit, conversion)), frame.CorrectionRate);
    if (!f.IsFinite(correction))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); correction = 0; return false; }
    bool valid = true;
    // As in the exact path, only a correcting face needs every corner at full strength.
    for (int corner = 0; corner < 3; corner++)
        if (f.NotEqual(data.Vertices[(int)data.Indices[triangle * 3 + corner]].Strength, 1f))
        { tally.Note(CoverageFailureCode.FoldedPatch, slot); valid = false; }
    if (valid) return true;
    correction = 0;
    return false;
}
```

**`CoverageReferenceCorrection.GatherGroup`** from `CoverageReferenceCorrection.cs`.

```csharp
static void GatherGroup(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int g, int representative)
{
    var gathers = scratch.ReferenceTopology;
    var record = gathers.Vertices[representative];
    int slot = scratch.RegionCommands[record.RegionIndex];
    f.At(CoverageExactnessPhase.GroupCorrection, g, slot);
    float correction = 0;
    for (int i = gathers.CoreFaceOffsets[g]; i < gathers.CoreFaceOffsets[g + 1]; i++)
        correction = f.Max(correction, scratch.FaceCorrections[gathers.CoreFaces[i]]);
    scratch.Corrections[g] = correction;
    float total = f.Equal(correction, 0) ? scratch.FullRecessions[g] : f.Add(scratch.FullRecessions[g], correction);
    scratch.FullRecessions[g] = total;
    if (f.NotEqual(correction, 0))
    {
        Vector3 before = scratch.Before[representative].Position;
        Vector3 d = scratch.Frames[scratch.RegionCommands[record.RegionIndex]].Recess;
        scratch.GroupPositions[g] = new Vector3(f.Sub(before.X, (f.Mul(total, d.X))), f.Sub(before.Y, (f.Mul(total, d.Y))), f.Sub(before.Z, (f.Mul(total, d.Z))));
    }
}
```

**`CoverageReferenceCorrection.CheckFullTarget`** from `CoverageReferenceCorrection.cs`.

```csharp
static void CheckFullTarget(CoverageReferenceArithmetic f, int vertex, Vector3 before, Vector3 full, float total, int slot,
    ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, ref CoverageTally tally)
{
    f.At(CoverageExactnessPhase.FullTarget, vertex, slot);
    var frame = scratch.Frames[slot];
    var command = commands[frame.Command];
    if (!f.IsFinite(total) || !CoverageReferenceMath.IsFinite3(f, full))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return; }
    Vector3 delta = new Vector3(f.Sub(full.X, before.X), f.Sub(full.Y, before.Y), f.Sub(full.Z, before.Z));
    Vector3 world = CoverageReferenceMath.Linear(f, delta, frame.ActorLinear);
    float distance = f.Sqrt(CoverageReferenceMath.Dot3(f, world, world));
    Vector3 q = CoverageReferenceMath.Point(f, full, frame.HostToCoverage);
    float lower = f.Add(command.MinZ, frame.CapMargin), upper = f.Sub(command.MaxZ, frame.CapMargin);
    if (!CoverageReferenceMath.IsFinite3(f, delta) || !CoverageReferenceMath.IsFinite3(f, world) || !f.IsFinite(distance)
        || !CoverageReferenceMath.IsFinite3(f, q) || !f.IsFinite(lower) || !f.IsFinite(upper))
    { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return; }
    if (f.Greater(distance, command.MaxWorldRecession)) tally.Note(CoverageFailureCode.RecessionExceeded, slot);
    if (f.Less(q.Z, lower) || f.Greater(q.Z, upper)) tally.Note(CoverageFailureCode.CapContact, slot);
}
```

**`CoverageReferenceCorrection.TransitionGroup`** from `CoverageReferenceCorrection.cs`.

```csharp
static void TransitionGroup(CoverageReferenceArithmetic f, ReadOnlySpan<CoverageCommandPose> commands,
    CoverageReferenceScratch scratch, int g, int representative)
{
    var data = scratch.ReferenceTopology;
    var record = data.Vertices[representative];
    var frame = scratch.Frames[scratch.RegionCommands[record.RegionIndex]];
    f.At(CoverageExactnessPhase.Transition, g, scratch.RegionCommands[record.RegionIndex]);
    float weight = commands[frame.Command].TransitionWeight;
    Vector3 before = scratch.Before[representative].Position;
    if (f.Equal(weight, 0)) scratch.GroupPositions[g] = before;
    else if (f.NotEqual(weight, 1))
    {
        float applied = f.Mul(weight, scratch.FullRecessions[g]);
        scratch.GroupPositions[g] = new Vector3(f.Sub(before.X, (f.Mul(applied, frame.Recess.X))), f.Sub(before.Y, (f.Mul(applied, frame.Recess.Y))),
            f.Sub(before.Z, (f.Mul(applied, frame.Recess.Z))));
    }
}
```

**`CoverageReferenceCorrection.StoreTransition`** from `CoverageReferenceCorrection.cs`.

```csharp
static void StoreTransition(CoverageReferenceArithmetic f, ReadOnlySpan<CoverageCommandPose> commands,
    CoverageReferenceScratch scratch, int v, int g, int representative)
{
    var data = scratch.ReferenceTopology;
    var record = data.Vertices[representative];
    int slot = scratch.RegionCommands[record.RegionIndex];
    f.At(CoverageExactnessPhase.Transition, g, slot);
    if (f.Equal(commands[scratch.Frames[slot].Command].TransitionWeight, 0)) scratch.After[v] = scratch.Before[v];
    else scratch.After[v].Position = scratch.GroupPositions[g];
}
```

### Face validation and normal/tangent gathers

**`CoverageReferenceNormals.ValidateVertex`** from `CoverageReferenceNormals.cs`.

```csharp
static bool ValidateVertex(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int vertex)
{
    int region = scratch.ReferenceTopology.Vertices[vertex].RegionIndex;
    f.At(CoverageExactnessPhase.NormalGather, vertex, region < 0 ? -1 : scratch.RegionCommands[region]);
    return CoverageReferenceMath.IsFiniteVertex(f, scratch.Before[vertex]) && CoverageReferenceMath.IsFinite3(f, scratch.After[vertex].Position);
}
```

**`CoverageReferenceNormals.ValidateFace`** from `CoverageReferenceNormals.cs`.

```csharp
static bool ValidateFace(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch,
    int t, Span<int> signs, out CoverageFailureCode code)
{
    Basis checkedBasis;
    var data = scratch.ReferenceTopology;
    var face = data.Triangles[t];
    int slot = face.RegionIndex < 0 || scratch.ActiveCommands == 0 ? -1 : scratch.RegionCommands[face.RegionIndex];
    f.At(CoverageExactnessPhase.FaceValidation, t, slot);
    if (Changed(f, scratch, t) && !TryBasis(f, scratch, t, out checkedBasis, out code))
        return false;
    if (slot < 0) { code = default; return true; }
    var frame = scratch.Frames[slot];
    Vector3 a = CoverageReferenceMath.Point(f, scratch.After[(int)scratch.ReferenceTopology.Indices[t * 3]].Position, frame.HostToCoverage),
        b = CoverageReferenceMath.Point(f, scratch.After[(int)scratch.ReferenceTopology.Indices[(t * 3) + 1]].Position, frame.HostToCoverage),
        c = CoverageReferenceMath.Point(f, scratch.After[(int)scratch.ReferenceTopology.Indices[(t * 3) + 2]].Position, frame.HostToCoverage);
    Vector3 u = CoverageReferenceMath.Sub3(f, b, a), v = CoverageReferenceMath.Sub3(f, c, a);
    float area = f.Sub((f.Mul(u.X, v.Y)), (f.Mul(u.Y, v.X)));
    float scale = f.Mul(f.Sqrt(CoverageReferenceMath.Dot3(f, u, u)), f.Sqrt(CoverageReferenceMath.Dot3(f, v, v)));
    if (!CoverageReferenceMath.IsFinite3(f, a) || !CoverageReferenceMath.IsFinite3(f, b) || !CoverageReferenceMath.IsFinite3(f, c)
        || !f.IsFinite(area) || !f.IsFinite(scale))
    { code = CoverageFailureCode.UnsupportedTransform; return false; }
    // The magnitude threshold mirrors the double guard. The discrete orientation uses the B27 exact predicate.
    if (!(f.Greater(f.Abs(area), f.Mul(1e-9f, scale)))) { code = CoverageFailureCode.FoldedPatch; return false; }
    var orientation = f.Edge(a, b, c);
    if (!f.IsFinite(orientation.Value)) { code = CoverageFailureCode.UnsupportedTransform; return false; }
    if (orientation.Sign == 0 || signs[slot] != 0 && signs[slot] != orientation.Sign)
    { code = CoverageFailureCode.FoldedPatch; return false; }
    signs[slot] = orientation.Sign;
    code = default;
    return true;
}
```

**`CoverageReferenceNormals.Moved`** from `CoverageReferenceNormals.cs`.

```csharp
static bool Moved(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int vertex) => !CoverageReferenceMath.Equal3(f, scratch.Before[vertex].Position, scratch.After[vertex].Position);
```

**`CoverageReferenceNormals.Changed`** from `CoverageReferenceNormals.cs`.

```csharp
static bool Changed(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int triangle) =>
    Moved(f, scratch, (int)scratch.ReferenceTopology.Indices[triangle * 3]) || Moved(f, scratch, (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 1])
    || Moved(f, scratch, (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 2]);
```

**`CoverageReferenceNormals.TryBasis`** from `CoverageReferenceNormals.cs`.

```csharp
static bool TryBasis(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int triangle,
    out Basis basis, out CoverageFailureCode code)
{
    Vector3 n;
    Vector3 m;
    basis = default;
    int a = (int)scratch.ReferenceTopology.Indices[triangle * 3], b = (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 1], c = (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 2];
    Vector3 u = CoverageReferenceMath.Sub3(f, scratch.Before[b].Position, scratch.Before[a].Position), v = CoverageReferenceMath.Sub3(f, scratch.Before[c].Position, scratch.Before[a].Position),
        s = CoverageReferenceMath.Sub3(f, scratch.After[b].Position, scratch.After[a].Position), t = CoverageReferenceMath.Sub3(f, scratch.After[c].Position, scratch.After[a].Position);
    Vector3 normal = CoverageReferenceMath.Cross(f, u, v), finalNormal = CoverageReferenceMath.Cross(f, s, t);
    if (!Unit(f, normal, out n, out code) || !Unit(f, finalNormal, out m, out code)) return false;
    float alignment = CoverageReferenceMath.Dot3(f, normal, finalNormal);
    float beforeDet = CoverageReferenceMath.Dot3(f, u, CoverageReferenceMath.Cross(f, v, n)),
        afterDet = CoverageReferenceMath.Dot3(f, s, CoverageReferenceMath.Cross(f, t, m));
    float beforeScale = f.Sqrt(f.Mul(CoverageReferenceMath.Dot3(f, u, u), CoverageReferenceMath.Dot3(f, v, v))),
        afterScale = f.Sqrt(f.Mul(CoverageReferenceMath.Dot3(f, s, s), CoverageReferenceMath.Dot3(f, t, t)));
    if (!f.IsFinite(alignment) || !f.IsFinite(beforeDet) || !f.IsFinite(afterDet)
        || !f.IsFinite(beforeScale) || !f.IsFinite(afterScale))
    { code = CoverageFailureCode.UnsupportedTransform; return false; }
    if (!(f.Greater(alignment, 0)) || !(f.Greater(beforeDet, 0)) || !(f.Greater(afterDet, 0))
        || f.LessOrEqual(beforeDet, f.Mul(beforeScale, 1e-12f)) || f.LessOrEqual(afterDet, f.Mul(afterScale, 1e-12f)))
    { code = CoverageFailureCode.FoldedPatch; return false; }
    Vector3 bu = Divide(f, CoverageReferenceMath.Cross(f, v, n), beforeDet), bv = Divide(f, CoverageReferenceMath.Cross(f, n, u), beforeDet),
        bn = Divide(f, normal, beforeDet), au = Divide(f, CoverageReferenceMath.Cross(f, t, m), afterDet),
        av = Divide(f, CoverageReferenceMath.Cross(f, m, s), afterDet), an = Divide(f, finalNormal, afterDet);
    if (!CoverageReferenceMath.IsFinite3(f, bu) || !CoverageReferenceMath.IsFinite3(f, bv) || !CoverageReferenceMath.IsFinite3(f, bn)
        || !CoverageReferenceMath.IsFinite3(f, au) || !CoverageReferenceMath.IsFinite3(f, av) || !CoverageReferenceMath.IsFinite3(f, an))
    { code = CoverageFailureCode.UnsupportedTransform; return false; }
    basis = new Basis(u, v, n, s, t, m, bu, bv, bn, au, av, an);
    return true;
}
```

**`CoverageReferenceNormals.Unit`** from `CoverageReferenceNormals.cs`.

```csharp
static bool Unit(CoverageReferenceArithmetic f, Vector3 value, out Vector3 unit, out CoverageFailureCode code)
{
    unit = default;
    float length = f.Sqrt(CoverageReferenceMath.Dot3(f, value, value));
    code = CoverageFailureCode.UnsupportedTransform;
    if (!CoverageReferenceMath.IsFinite3(f, value) || !f.IsFinite(length)) return false;
    if (!(f.Greater(length, 0))) { code = CoverageFailureCode.FoldedPatch; return false; }
    unit = Divide(f, value, length);
    if (!CoverageReferenceMath.IsFinite3(f, unit)) return false;
    code = default;
    return true;
}
```

**`CoverageReferenceNormals.Divide`** from `CoverageReferenceNormals.cs`.

```csharp
static Vector3 Divide(CoverageReferenceArithmetic f, Vector3 value, float divisor) => new Vector3(f.Div(value.X, divisor), f.Div(value.Y, divisor), f.Div(value.Z, divisor));
```

**`CoverageReferenceNormals.Combine`** from `CoverageReferenceNormals.cs`.

```csharp
static Vector3 Combine(CoverageReferenceArithmetic f, Vector3 a, float x, Vector3 b, float y, Vector3 c, float z) => new Vector3(
    f.Add((f.Add((f.Mul(a.X, x)), (f.Mul(b.X, y)))), (f.Mul(c.X, z))), f.Add((f.Add((f.Mul(a.Y, x)), (f.Mul(b.Y, y)))), (f.Mul(c.Y, z))), f.Add((f.Add((f.Mul(a.Z, x)), (f.Mul(b.Z, y)))), (f.Mul(c.Z, z))));
```

**`CoverageReferenceNormals.Gather`** from `CoverageReferenceNormals.cs`.

```csharp
static bool Gather(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, ReadOnlySpan<int> corners,
    bool tangent, out Vector3 direction, out CoverageFailureCode code)
{
    Vector3 sum = default;
    foreach (int corner in corners)
    {
        Basis basis;
        Vector3 unit;
        int vertex = (int)scratch.ReferenceTopology.Indices[corner];
        if (!Moved(f, scratch, vertex)) continue;
        var authored = scratch.Before[vertex];
        Vector3 original = tangent ? new Vector3(authored.Tangent.X, authored.Tangent.Y, authored.Tangent.Z) : authored.Normal;
        if (CoverageReferenceMath.Equal3(f, original, default(Vector3))) continue;
        if (!TryBasis(f, scratch, corner / 3, out basis, out code)) { direction = default; return false; }
        Vector3 transformed = tangent
            ? Combine(f, basis.S, CoverageReferenceMath.Dot3(f, basis.Bu, original), basis.T, CoverageReferenceMath.Dot3(f, basis.Bv, original),
                basis.M, CoverageReferenceMath.Dot3(f, basis.Bn, original))
            : Combine(f, basis.Au, CoverageReferenceMath.Dot3(f, basis.U, original), basis.Av, CoverageReferenceMath.Dot3(f, basis.V, original),
                basis.An, CoverageReferenceMath.Dot3(f, basis.N, original));
        if (!Unit(f, transformed, out unit, out code)) { direction = default; return false; }
        sum = new Vector3(f.Add(sum.X, unit.X), f.Add(sum.Y, unit.Y), f.Add(sum.Z, unit.Z));
        if (!CoverageReferenceMath.IsFinite3(f, sum)) { direction = default; code = CoverageFailureCode.UnsupportedTransform; return false; }
    }
    return Unit(f, sum, out direction, out code);
}
```

**`CoverageReferenceNormals.UpdateVertex`** from `CoverageReferenceNormals.cs`.

```csharp
static bool UpdateVertex(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch,
    int vertex, out CoverageFailureCode code)
{
    var rows = scratch.ReferenceTopology;
    int region = scratch.ReferenceTopology.Vertices[vertex].RegionIndex;
    f.At(CoverageExactnessPhase.NormalGather, vertex, region < 0 ? -1 : scratch.RegionCommands[region]);
    var before = scratch.Before[vertex];
    var result = before;
    if (!Moved(f, scratch, vertex)) { scratch.Published[vertex] = result; code = default; return true; }
    result.Position = scratch.After[vertex].Position;
    int shading = scratch.ReferenceTopology.Vertices[vertex].ShadingGroup;
    if (!CoverageReferenceMath.Equal3(f, before.Normal, default(Vector3)))
    {
        Vector3 normal;
        if (!Gather(f, scratch, rows.ShadingCorners.AsSpan(rows.ShadingCornerOffsets[shading],
            rows.ShadingCornerOffsets[shading + 1] - rows.ShadingCornerOffsets[shading]), false, out normal, out code))
            return false;
        result.Normal = normal;
    }
    if (f.NotEqual(before.Tangent.X, 0) || f.NotEqual(before.Tangent.Y, 0) || f.NotEqual(before.Tangent.Z, 0))
    {
        Vector3 tangent;
        if (!Gather(f, scratch, rows.VertexCorners.AsSpan(rows.VertexFaceOffsets[vertex],
            rows.VertexFaceOffsets[vertex + 1] - rows.VertexFaceOffsets[vertex]), true, out tangent, out code))
            return false;
        result.Tangent = new Vector4(tangent, before.Tangent.W);
    }
    scratch.Published[vertex] = result;
    code = default;
    return true;
}
```

The gather loops above consume the CPU rows in their stored order. Core correction starts at +0
and applies `f.Max` once per incident core face. Normal/tangent accumulation starts at a zero vector
and applies one `f.Add` per component per accepted corner. A tree reduction, float atomic scatter,
corner deduplication or averaging changes the graph and is forbidden. Normal gathering uses shading
rows. Tangent gathering uses each vertex's own corner rows and preserves its original tangent W.

`CoverageReferenceCorrection.cs`, `CoverageReferenceCorrection.TryFace`, allocates host scratch and
sequences `PrepareFace`, `CoverageReferenceSupport.TryOverlayMaximum`, then `FinishFace`. Membership
and discrepancy precede deficit. Only a positive finite deficit leads to correction and then the
full-strength-corner check, as B25 requires. The support overlay wrapper allocates its stack spans
on the host before calling `CoverageReferenceSupport.OverlayMaximum`.

## 5. Compilation

| Backend | Required compilation and lowering |
| --- | --- |
| Vulkan | GLSL `precise` on every intermediate in these numerical kernels, including called helper intermediates. Preserve no-contraction and evaluation dependencies in emitted SPIR-V. |
| Metal | Fast maths off for the entire coverage library, including correction and predicate helpers. Use correctly rounded operations under that mode and retain scalar dependencies. |
| Direct3D11 | FXC with `D3DCOMPILE_IEEE_STRICTNESS`. Preserve scalar add/subtract/multiply order. No relaxed `rcp` or `rsq` substitution for `/` or `sqrt`. |

Part C inspects emitted code and records that the compiler kept the operation graph and explicit
flushes. This is the lowering obligation formerly called RV2. A source qualifier alone is not
proof. Code generation, unit kernels and native fixture comparisons must agree. An optimization
that changes a branch, zero sign, intermediate rounding or gather order is a failure.

## 6. Division and square root

`CoverageReferenceArithmetic.cs`, `CoverageReferenceArithmetic.Div` and `Sqrt`, define correctly
rounded binary32 results before the final flush. Native accuracy alone is insufficient where it
is not correct rounding. The retained NC2 backend bounds determine the correction obligation:

| Backend | Native division | Native square root | Required disposition |
| --- | --- | --- | --- |
| Vulkan | 2.5 ULP for divisor magnitude in `[2^-126,2^126]`. | Accuracy inherited from `1/inversesqrt(x)`. | Correct the native candidate. Prove the searched-neighbor bound over the admitted domain. |
| Metal, fast maths off | Correctly rounded. | Correctly rounded. | Verify this mode and emitted instruction, then retain the native correctly rounded result. |
| Direct3D11 | Reciprocal within 1 ULP followed by multiply, or better. | Within 1 ULP. | Correct the native candidate. Do not assume a one-neighbor search follows from the reciprocal bound. |

On a backend requiring correction, take the native candidate and search adjacent binary32 words
within a proven bound. Determine the correctly rounded neighbor by an exact residual sign test,
built from verified correctly rounded add, subtract and multiply using TwoSum and Veltkamp-Dekker
TwoProduct, or from 32-bit integer multiword arithmetic. Do not use a rounded residual or a guessed
ULP allowance. The reference result is unique.

For finite division, compare the exact quotient with candidate midpoints by the exact sign of
`a - b*midpoint`, accounting for the divisor sign. For nonnegative finite square root, compare the
root with a nonnegative midpoint by the exact sign of `a - midpoint*midpoint`. Midpoints and these
residuals are exact quantities, not extra rounded inventory expressions. If an error-free float
implementation cannot retain all exact residual components with the device's denormal behavior,
use exact integer arithmetic or a proved scaling method. Do not silently flush a needed component
of an exact helper's representation.

Part C proves for each backend that the native error bound encloses the correctly rounded result
within the searched neighbors. Outside the stated Vulkan divisor range, obtain a proved bound by
scaling or use an exact path. A bound for a different instruction or precision mode does not count.
Handle zero, signs, invalid inputs and infinities before applying a finite-neighbor search.
Preserve correctly rounded overflow to infinity rather than saturating to the largest finite word.

After the operand flush, no operand is subnormal or the smallest normal. For normal operands and
normal results, binary32 division and square root are never exactly halfway between adjacent
binary32 values, so no tie rule is needed in that domain. Still test near-midpoint cases and
binade boundaries. Correct a tiny result to RN32 first, then apply the inclusive flush. Flushing
the native approximation before correction could discard a result whose corrected word survives.

## 7. Exact predicates and the crossing parameter

`CoverageReferencePredicates.cs`, `CoverageReferencePredicates.Edge`, `Dot`, `Scalar` and
`CompareMagnitude`, provide B27's mathematical results. The arithmetic-home entry points first
flush the input XY coordinates. For those coordinates, return:

- `Edge(a,b,p)`: exact sign and RN32 of `(b.X-a.X)*(p.Y-a.Y) - (b.Y-a.Y)*(p.X-a.X)`.
- `Dot(a,b,c,d)`: exact sign and RN32 of `(b.X-a.X)*(d.X-c.X) + (b.Y-a.Y)*(d.Y-c.Y)`.

These displayed polynomials mean exact real evaluation of binary32 inputs, followed by one RN32
conversion. They are not sequences of ordinary binary32 subtractions and products. An exact zero
has sign 0 and value +0. A nonzero exact value that rounds to zero retains its mathematical sign
in `Sign` and its sign bit in `Value`. Flush the RN32 value using section 8 while retaining the exact
sign. A zero rounded value does not imply a zero exact sign. Invalid XY input returns sign zero
and NaN, which the caller's finiteness guard rejects. Z is not a predicate input.

The CPU reference expands binary32 products in binary64 and compares the exact expansion against
adjacent binary32 midpoints, including the overflow midpoint. Part C need not duplicate that
representation. It must return the same exact sign and rounded word, including ties to even at a
binary32 midpoint. Float error-free expansions are allowed only after correctly rounded add,
subtract and multiply without contraction are verified on that backend, including exactness under
its denormal behavior. Otherwise use 32-bit integer multiword arithmetic. Never make a branch from
a rounded determinant when the source branches on `Sign`.

B30 is `CoverageReferencePredicates.cs`, `CoverageReferencePredicates.TryScaledSides`, reached through
`CoverageReferenceArithmetic.cs`, `CoverageReferenceArithmetic.CrossingParameter`. Compute the two
exact determinants `ca = Edge(s,t,a)` and `cb = Edge(s,t,b)`, using their exact values rather than
already-rounded `Value` fields. Both must be nonzero and opposite in sign. Choose the common integer
`k` such that `max(abs(ca),abs(cb))*2^k` is in `[1,2)`. Scale exactly before either conversion:

```text
x = F(RN32(ca * 2^k))
y = F(RN32(cb * 2^k))
quotient = f.Div(x, f.Sub(x, y))
u = f.Clamp(quotient, 0, 1)
```

The first two lines are exact scaling, one RN32 conversion and the B38 flush. The last two lines
are inventory operations. The larger rounded side has magnitude at least one. Opposite signs
therefore keep the divisor magnitude at least one, even if the smaller side flushes. Do not divide
unscaled tiny sides, which could become 0/0. `TryScaledSides` refuses nonfinite, zero or same-sign
sides. Its home wrapper returns NaN on that path. Exact endpoint and collinear branches in
`CoverageReferenceSupport.OverlayPair` avoid the division entirely. The crossing point and both
height evaluations retain their section 4 binary32 trees.

## 8. Flush rule

B38 replaces subnormal rejection. Use this bit rule on every inventory operand and every inventory
result, including comparisons and finite tests, and on every uploaded scalar's first numerical
read or copied-field admission:

```text
bits = as_uint(value)
magnitude = bits & 0x7fffffff
F(value) = magnitude <= 0x00800000 ? as_float(bits & 0x80000000) : value
```

Both signed zeros stay unchanged. Every nonzero magnitude at or below `2^-126`, including the
smallest normal, becomes a zero of the same sign. Larger normal words, infinities and NaNs stay
unchanged by this rule. `CoverageReferenceArithmetic.cs`, `CoverageReferenceArithmetic.Flush`,
skips an already-zero word for diagnostic counting but produces the same bits as this rule.

Emit the integer compare and select explicitly after each operation. Do not rely on a hardware
denormal mode, compiler defaults, an entry-only flush or a final-output-only flush. Including the
smallest normal makes before-rounding and after-rounding hardware tininess modes agree. Correct
division and square root to RN32 before the result flush. B27 exact signs remain exact on flushed
input coordinates while their RN32 values flush. B30's scaled sides flush after their conversion.

`CoverageReferenceMath.cs`, `CoverageReferenceMath.ReadVertex`, flushes every admitted vertex field
through the by-reference finite test. Source validation uses a private flushed copy and does not
mutate the caller. Skinning flushes copied color, UV and retained normal/tangent fields as well as
computed output. Float bit copies thereafter retain those words, including signed zeros.

`CoverageReferenceArithmetic.EventCount`, `FirstSite` and `FirstSlot` diagnose flushes. They do not
reject a sample, create a device-exactness oracle stage or change a refusal code. The trace retains
the first original value bits and its phase, element and sorted slot. The dev-only real-GPU mode
logs the first differing vertex whatever the cause, with the matching pose and differing words.

## 9. Control flow

### Host sequencing and barriers

`CoverageReferenceConstructor.cs`, `CoverageReferenceConstructor.TryConstruct`, sequences frame
preparation, source/refinement interpolation, point conformation, core correction, transition and
normal update, then performs one caller-output copy. `TryInterpolate` validates source vertices
in source index order, validates aliases, computes canonical groups, validates every generated
attribute and only then fills `Before`. Generated attributes may be evaluated again for the fill.
Original vertices retain their own admitted posed record.

`CoverageReferenceConstructor.TryConformPrepared` clears all recession scalars and representatives,
initializes groups from `Before`, and chooses the first positive-strength vertex in each active
region as its group representative. It visits those representatives in vertex-index order,
independent of position-group numbering or command submission order. A group is core when its
core-face row is nonempty. One conformation result is copied to every active alias.

`CoverageReferenceCorrection.cs`, `CoverageReferenceCorrection.TryApplyPrepared`, clears correction
and guard arrays. It evaluates core faces in sorted slot order and then triangle order against an
immutable pointwise `After` surface. Finish every face before any group gather. Refuse on a failed
face tally before moving groups. Gather each active group's incident corrections, then check full
targets in representative vertex order. Refuse on that tally before copying group positions to
aliases. Transition happens once after full-target checks, including when transition weight is zero.
Normal validation and gathering then consume immutable `Before` and transitioned `After`.

`CoverageReferenceNormals.cs`, `CoverageReferenceNormals.TryUpdate`, validates all vertices, then
all faces in triangle order, then updates vertices in vertex order. Per-command orientation signs
begin at integer zero. Finish all face validation before publishing any attribute.

This is B37's host/kernel split. Host loop bodies are not extra floating-point operations. Part C
can dispatch groups or vertices independently, but must reproduce this data flow, each local
reduction order and the deterministic guard order. Test observers, stack scratch and argument
validation do not become shader instructions.

### Grid definition, B16

`CoverageReferenceSupport.cs`, `CoverageReferenceSupport.Cell`, `Bounds`, `VisitCells` and `TryHeight`,
define the query, rather than accelerating a full scan without a prefilter. Bin each binary32
triangle AABB by the same monotone cell function used by the query. Bounds are inclusive. A point
must be finite and within the global bounds. Visit only its cell's candidate list in source order,
skip a triangle whose inclusive AABB excludes it, then accept the first candidate whose exact
edge signs match its exact area sign. A candidate with invalid area/value or nonfinite height
fails that query as in the transcribed body. Do not continue past that failure to find another hit.

No accepted candidate means the caller's boundary or miss path. A point accepted by edge rounding
outside an AABB is not a hit. The cell expression is the subtract/divide/multiply tree followed by
a count of comparisons against 1 through 15, not a float-to-int cast. It returns 0 for NaN and
negative infinity and 15 for positive infinity. The global maximum belongs to cell 15. Keep the
same result for degenerate ranges and neighbors of cell/AABB boundaries.

### Aliases, B19

`CoverageReferenceConstructor.cs`, `CoverageReferenceConstructor.AliasesEqual`, `TryInterpolate` and
`TryConformPrepared`, compare aliases by value, so +0 and -0 are the same geometric point. Refuse
`FoldedPatch` on a value mismatch. Original protected vertices copy their own flushed posed bits.
Computed refined vertices and active conformed groups use a canonical group position. Do not
canonicalize protected zero sign bits merely because another alias has a different zero sign.

### Guard reduction and first-code attribution

`CoverageEvaluator.cs`, `CoverageTally.Note` and the `ReadOnlySpan<int>` overload of
`CoverageTally.Succeeded`, define the reduction below. The `CoverageScratch` overload is an
unmarked exact-path host wrapper, not the reference entry point. Reference calls pass `Order`.
The first noted code and sorted slot win. Count only later notes with that exact code and slot,
saturating at `int.MaxValue`. Other codes/slots do not replace the first or increase its count.

**`CoverageTally.Note`** from `CoverageEvaluator.cs`.

```csharp
internal void Note(CoverageFailureCode failure, int commandSlot)
{
    if (code == CoverageFailureCode.None) { code = failure; slot = commandSlot; count = 1; }
    else if (code == failure && slot == commandSlot && count < int.MaxValue) count++;
}
```

**`CoverageTally.Succeeded`** from `CoverageEvaluator.cs`.

```csharp
internal readonly bool Succeeded(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
    ReadOnlySpan<int> slotCommands, out CoverageFailure failure)
{
    if (code == CoverageFailureCode.None) { failure = default; return true; }
    if (slot < 0) return CoverageFrames.Refuse(topology, code, 0, 0, count, 0, out failure);
    CoverageCommandPose command = commands[slotCommands[slot]];
    return CoverageFrames.Refuse(topology, code, command.EquipmentInstanceId, command.Id, count, 0, out failure);
}
```

For parallel guard evaluation retain the first event by the reference traversal key, then count
matching code/slot events across that stage. In conformation the key is representative vertex
index, then the member's local note order. In face correction it is sorted slot, triangle index,
then corner/local note order. In full-target checking it is representative vertex index, then
recession note before cap note. Stage completion precedes the next stage, so a later stage never
replaces an earlier failure. An arbitrary first atomic writer is not this reduction.

Source/alias/generated-attribute validation stops at its first failure. Normal validation stops
at the first invalid vertex, then the first invalid triangle in triangle order, then the first
failed vertex gather in vertex order. These paths use the corresponding refusal helper rather
than a tally, with equipment ID, command ID, count and limit zero. `CoverageReferenceNormals.cs`,
`CoverageReferenceNormals.Refuse`, and `CoverageReferenceConstructor.cs`,
`CoverageReferenceConstructor.Refuse`, retain the host identity. Tally attribution instead maps
its winning slot through `Order` and fills the equipment and command IDs. Its limit is zero.
`CoverageCommandPose.cs`, `CoverageFrames.Refuse`, constructs that failure record.

| Numerical refusal | Code from `CoverageFailure.cs`, `CoverageFailureCode` | Trigger in the reference members above |
| --- | --- | --- |
| `UnsupportedSupport` | 3 | A required boundary query fails. |
| `UnsupportedTransform` | 4 | Nonfinite admitted vertex, intermediate, overlay, basis or corrected output, or invalid correction terms. |
| `CoreEscaped` | 5 | Core point misses support, or an actual/nominal correction corner misses support. |
| `CapContact` | 6 | Nominal point or validated full target is outside the inclusive cap band. |
| `RecessionExceeded` | 7 | Pointwise world recession or full-target world distance exceeds the command cap. |
| `FoldedPatch` | 8 | Alias value mismatch, correcting core corner below full strength, collapsed direction/basis, near-zero face area or orientation mismatch. |

Admission additionally retains `UnknownRegion` (1), `IdentityMismatch` (2),
`OverlappingConformers` (11), `CapacityExceeded` (12) and `StaleFrame` (13), with support/transform
codes as in `CoverageFrameAdmission.cs`, `CoverageFrameAdmission.TryCommand`.
`EquipmentIntersection` (9) and `HostIntersection` (10) are certification concerns, not new
construction kernel tests. `UnsupportedAdapter` (14) is not emitted by these reference numerical
members. No flush refusal is added. Keep each existing code and its precedence.

### Atomic publication

`CoverageReferenceConstructor.cs`, `CoverageReferenceConstructor.TryConstruct`, leaves caller output
untouched on failure, and copies `Published` only after every phase succeeds.
`CoverageReferenceNormals.cs`, `CoverageReferenceNormals.UpdateVertex`, may have filled private
candidate records before a later gather fails. Those records are not publishable.

The GPU chain preserves this all-or-nothing actor decision. Any refusal sets the actor's refusal
flag by integer atomic OR. After all numerical work and guard completion, select the complete
uncovered current pose for a refused actor or the complete candidate for a successful actor. No
partial conformer, alias subset or normal update may escape. The atomic flag chooses the actor
outcome. The deterministic reduction separately supplies reference-equivalent failure attribution.

## 10. Verification

Part C runs native equivalence on Vulkan, Metal and Direct3D11 CI devices, in production mode with
empty overrides, for all 72 frozen cases. Compare every binary32 output word in vertex and field
order against the reference. This includes position, normal, color, UV and tangent, preserving zero
signs, seams, original copies and weights 0, 0.5 and 1. Compare success/refusal codes and attribution
on the construction negatives, and prove a refusal publishes the full uncovered pose.

The frozen expectation is
`KhaozEngine.Render.Tests/Fixtures/coverage-reference-v1/outputs.sha256`, created in Task 6A and
unchanged at `505337052`. It contains 144 hashes, production and like-for-like constructions for
72 cases. Production native tests use the production rows. Like-for-like rows continue to guard
the reference test seam. Keep `CoverageReferenceOutputTests.cs`,
`CoverageReferenceOutputTests.Every_fixture_reference_output_matches_the_hash_manifest`, passing.
Use `CoverageReferenceOutputTests.cs`, `CoverageReferenceOutputs.Hash`, and
`CoverageReferenceOutputs.ComputeAll` for the existing serialized-word order and mode mapping.
Do not refresh the manifest as a consequence of a device mismatch.

Unit kernels exercise every inventory row, both zero signs, inclusive flush threshold and next
normal, NaN/infinity guard paths, min/max zero ties, clamp zero preservation and bit stores.
Division and square-root cases cover the proved native candidate-error domain, adjacent binades,
near-midpoints, underflow, results rounding to the smallest normal, largest finite results and
overflow. They compare corrected pre-flush RN32 and final flushed words independently.

Predicate kernels compare exact sign and RN32 value against a BigInteger oracle, including a
constructed exact binary32 midpoint, near-collinearity, exact endpoints, opposite tiny sides,
large exponent separation and the B30 scaling boundary. Check signs independently of rounded or
flushed values. Device float-expansion proofs include the no-contraction and denormal cases that
make their representation exact. An integer method must prove its limb arithmetic and rounding.

Recheck B16 boundary/AABB neighbors, B19 zero-sign aliases, B25 nonpositive deficits with partial
strength, command submission reordering, immutable face/group data flow, gather order, full-target
checks at zero transition and refusal-then-success reset. Retain emitted-code evidence for section 5
and per-backend correction proofs for section 6. Real-GPU verification is dev-only and asynchronous,
logging the first differing vertex, pose and output words. CI software or virtual GPUs do not replace
that driver-conformance check.

Open transcription items: none. Part C owns the GPU implementations, emitted-code evidence,
arithmetic proofs and native comparisons described here.
