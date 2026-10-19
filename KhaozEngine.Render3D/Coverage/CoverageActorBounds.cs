using System;
using System.Numerics;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Render3D;

/// <summary>Cold influence boxes and allocation-free current-palette envelopes. This is host preparation,
/// not reference construction. The same refined, recession-expanded world box feeds every drawing decision.</summary>
internal sealed class CoverageActorBounds
{
    const double U = 1d / 16777216, FlushLimit = 1.1754943508222875e-38;
    readonly string hostIdentity;
    readonly CoverageBoundedVector sourceBox;
    readonly CoverageBoundedVector[] boneBoxes;
    readonly bool[] influenced;
    readonly CoverageScalarBounds[] slotWeights;
    readonly CoverageScalarBounds totals;
    readonly bool nonnegative = true, identity, rigid;
    readonly CoverageOrdinaryBounds ordinary;

    internal CoverageActorBounds(CoverageSourceTopology source)
        : this((source ?? throw new ArgumentNullException(nameof(source))).Identity, source.Positions, source.SkinVertices) { }

    internal CoverageActorBounds(ReadOnlySpan<SkinnedVertex> vertices) : this("ordinary-skinned-mesh", [], vertices) { }

    CoverageActorBounds(string identityName, ReadOnlySpan<Vector3> positions, ReadOnlySpan<SkinnedVertex> skin)
    {
        hostIdentity = identityName;
        sourceBox = EmptyBox;
        foreach (Vector3 p in positions) sourceBox = Join(sourceBox, Read(p));
        if (positions.IsEmpty)
            foreach (ref readonly var vertex in skin) sourceBox = Join(sourceBox, Read(vertex.Position));
        if (positions.IsEmpty && skin.IsEmpty) sourceBox = default;
        rigid = skin.IsEmpty;
        int bones = 0;
        foreach (ref readonly var v in skin)
            for (int slot = 0; slot < 4; slot++)
            {
                float index = v.BoneIndices[slot];
                if (!float.IsFinite(index) || index < 0 || index >= int.MaxValue || index != MathF.Truncate(index)
                    || !float.IsFinite(v.BoneWeights[slot])) throw new ArgumentException("Bounds require validated skin influences.", nameof(skin));
                bones = Math.Max(bones, checked((int)index + 1));
            }
        boneBoxes = new CoverageBoundedVector[bones];
        influenced = new bool[bones];
        slotWeights = new CoverageScalarBounds[bones * 4];
        Array.Fill(boneBoxes, EmptyBox);
        Array.Fill(slotWeights, Empty);
        totals = Empty;
        foreach (ref readonly var v in skin)
        {
            CoverageScalarBounds total = Zero;
            float roundedTotal = 0;
            for (int slot = 0; slot < 4; slot++)
            {
                int bone = (int)v.BoneIndices[slot];
                var weight = Read(v.BoneWeights[slot]);
                nonnegative &= v.BoneWeights[slot] >= 0;
                total = Add(total, weight);
                // Cold branch metadata uses the pinned float additions. Adding the first weight to
                // zero cannot change its threshold comparison, including either zero sign.
                roundedTotal = Flush(roundedTotal + Flush(v.BoneWeights[slot]));
                slotWeights[bone * 4 + slot] = Join(slotWeights[bone * 4 + slot], weight);
                if (v.BoneWeights[slot] == 0) continue;
                influenced[bone] = true;
                boneBoxes[bone] = Join(boneBoxes[bone], Read(v.Position));
            }
            totals = Join(totals, total);
            identity |= roundedTotal < 1e-8f;
        }
        ordinary = new(skin);
    }

    /// <summary>Ordinary rendering preserves GPU skin W, or drops CPU skin W before the full model transform.</summary>
    internal CoveragePositionBounds BoundOrdinary(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, bool vertexSkinning) =>
        ordinary.Transform(palette, model, vertexSkinning);

    internal MeshBounds BoundSkin(ReadOnlySpan<Matrix4x4> composedPalette)
    {
        if (rigid) return ToMesh(sourceBox);
        if (composedPalette.Length < boneBoxes.Length) return InvalidMesh;
        foreach (var matrix in composedPalette)
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 3; column++)
                    if (!float.IsFinite(matrix[row, column])) return InvalidMesh;
        var bound = nonnegative ? BoundJoined(composedPalette) : BoundSigned(composedPalette);
        if (identity) bound = Join(bound, sourceBox);
        return ToMesh(bound);
    }

    CoverageBoundedVector BoundJoined(ReadOnlySpan<Matrix4x4> palette)
    {
        var joined = EmptyBox;
        Span<double> products = stackalloc double[3];
        Span<double> coefficients = stackalloc double[12];
        products.Clear(); coefficients.Clear();
        for (int bone = 0; bone < boneBoxes.Length; bone++)
        {
            if (!influenced[bone]) continue;
            var matrix = Matrix(palette[bone]);
            joined = Join(joined, matrix.Apply(boneBoxes[bone]));
            for (int column = 0; column < 3; column++)
            {
                products[column] = Math.Max(products[column], ProductSum(boneBoxes[bone], matrix, column).Max);
                for (int row = 0; row < 4; row++)
                    coefficients[row * 3 + column] = Math.Max(coefficients[row * 3 + column], Abs(Read(palette[bone][row, column])));
            }
        }
        if (!joined.IsFinite) return identity && totals == Zero ? sourceBox : InvalidBox;
        var weight = new CoverageScalarBounds(0, totals.Max);
        // Bound every intermediate's absolute sum as well. Potential overflow cannot use the finite gamma model.
        foreach (double coefficient in coefficients)
        {
            var absolute = Multiply(Point(coefficient), weight);
            var error = Add(Multiply(Gamma(4), absolute), Divide(Point(7 * FlushLimit), Point(1 - 4 * U)));
            if (!Round32Envelope(Add(absolute, error)).IsFinite) return InvalidBox;
        }
        var positionSum = Add(Add(Point(Abs(sourceBox.X)), Point(Abs(sourceBox.Y))), Point(Abs(sourceBox.Z)));
        var flush = Divide(Add(Multiply(Point(7 * FlushLimit), Add(positionSum, One)), Point(6 * FlushLimit)), Point(1 - 8 * U));
        Span<CoverageScalarBounds> axes = stackalloc CoverageScalarBounds[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var absoluteSum = Multiply(Point(products[axis]), weight);
            var error = Add(Multiply(Gamma(8), absoluteSum), flush);
            if (!Round32Envelope(Add(absoluteSum, error)).IsFinite) return InvalidBox;
            axes[axis] = Expand(Multiply(Axis(joined, axis), totals), error.Max);
        }
        return new(axes[0], axes[1], axes[2]);
    }

    CoverageBoundedVector BoundSigned(ReadOnlySpan<Matrix4x4> palette)
    {
        Span<CoverageScalarBounds> matrix = stackalloc CoverageScalarBounds[12];
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 3; column++)
            {
                CoverageScalarBounds sum = Zero;
                for (int slot = 0; slot < 4; slot++)
                {
                    CoverageScalarBounds term = Empty;
                    for (int bone = 0; bone < boneBoxes.Length; bone++)
                    {
                        var weight = slotWeights[bone * 4 + slot];
                        if (!weight.IsFinite) continue;
                        term = Join(term, Mul32(Read(palette[bone][row, column]), weight));
                    }
                    sum = slot == 0 ? term : Add32(sum, term);
                }
                matrix[row * 3 + column] = sum;
            }
        Span<CoverageScalarBounds> axes = stackalloc CoverageScalarBounds[3];
        for (int axis = 0; axis < 3; axis++)
            axes[axis] = Add32(Add32(Add32(Mul32(sourceBox.X, matrix[axis]), Mul32(sourceBox.Y, matrix[3 + axis])),
                Mul32(sourceBox.Z, matrix[6 + axis])), matrix[9 + axis]);
        return new(axes[0], axes[1], axes[2]);
    }

    internal MeshBounds BoundRefinement(in MeshBounds skin)
    {
        var box = FromMesh(skin);
        if (!box.IsFinite) return InvalidMesh;
        Span<CoverageScalarBounds> axes = stackalloc CoverageScalarBounds[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var value = Axis(box, axis);
            var error = Add(Multiply(Gamma(6), Point(Abs(value))), Point(6 * FlushLimit));
            axes[axis] = Expand(value, error.Max);
        }
        return ToMesh(new(axes[0], axes[1], axes[2]));
    }

    internal MeshBounds ExpandWorld(in MeshBounds skin, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands)
    {
        var local = ConformedLocal(skin, model, commands, out var world);
        if (!local.IsFinite || !CoverageAffine.TryFrom(model, out var affine)) return InvalidMesh;
        var matrix = CoverageBoundedAffine.From(affine);
        Span<CoverageScalarBounds> axes = stackalloc CoverageScalarBounds[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var products = ProductSum(local, matrix, axis);
            var error = Add(Multiply(Gamma(7), products), Point(7 * FlushLimit));
            if (!Round32Envelope(Add(products, error)).IsFinite) return InvalidMesh;
            error = Add(error, FieldError(local, model, axis));
            axes[axis] = Expand(Axis(world, axis), error.Max);
        }
        return ToMesh(new(axes[0], axes[1], axes[2]));
    }

    CoverageBoundedVector ConformedLocal(in MeshBounds skin, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands,
        out CoverageBoundedVector world)
    {
        world = InvalidBox;
        var refined = FromMesh(BoundRefinement(skin));
        double recession = MaxRecession(commands);
        if (!refined.IsFinite || !double.IsFinite(recession) || !CoverageAffine.TryFrom(model, out var affine)) return InvalidBox;
        var matrix = CoverageBoundedAffine.From(affine);
        var worldBefore = matrix.Apply(refined);
        if (commands.IsEmpty) { world = worldBefore; return refined; }
        if (!CoverageFrameAdmission.TryActor(model, out var actor) || !actor.TryInverse(out var actorInverse)
            || !CoverageBoundedAffine.From(actor).TryInverse(out var inverse)) return InvalidBox;
        Span<CoverageScalarBounds> domain = stackalloc CoverageScalarBounds[3];
        Span<CoverageScalarBounds> worldAxes = stackalloc CoverageScalarBounds[3];
        Span<CoverageScalarBounds> moveErrors = stackalloc CoverageScalarBounds[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var column = new CoverageBoundedVector(Axis(inverse.R1, axis), Axis(inverse.R2, axis), Axis(inverse.R3, axis));
            // Keep C1's direction-independent inverse-column domain as well as the actual rounded moves.
            domain[axis] = Join(Axis(refined, axis), Round32Envelope(Expand(Axis(refined, axis), Multiply(Point(recession), column.Length).Max)));
            worldAxes[axis] = Expand(Axis(worldBefore, axis), recession);
        }
        var linear = Matrix(model with { M41 = 0, M42 = 0, M43 = 0 });
        foreach (var command in commands)
        {
            if (!CoverageAffine.TryFrom(command.CoverageWorld, out var coverage)) return InvalidBox;
            // Match Derive's binary64 inverse/application and round-once upload, then enclose the
            // device field read's F. The ideal inverse direction alone does not enclose these words.
            var direction = actorInverse.ApplyLinear(coverage.Row(2));
            var recess = Read(new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z));
            double maximum = RecessionScalar(refined, linear, recess, command);
            if (!recess.IsFinite || !double.IsFinite(maximum)) return InvalidBox;
            for (int axis = 0; axis < 3; axis++)
            {
                var move = Mul32(new(0, maximum), Axis(recess, axis));
                var conformed = Round32Envelope(Subtract(Axis(refined, axis), move));
                domain[axis] = Join(domain[axis], conformed);
                var error = Add(Multiply(Gamma(2), Multiply(Point(maximum), Point(Abs(Axis(recess, axis))))),
                    Divide(Add(Multiply(Point(U), Point(Abs(Axis(refined, axis)))), Point(2 * FlushLimit)), Point(1 - 2 * U)));
                moveErrors[axis] = new(-error.Max, error.Max);
            }
            // Preserve the common scalar through the exact model map. Transforming the independent
            // local axis intervals would lose inverse/shear cancellation and needlessly inflate culling.
            var worldDirection = matrix.ApplyLinear(recess);
            var worldError = matrix.ApplyLinear(new(moveErrors[0], moveErrors[1], moveErrors[2]));
            for (int axis = 0; axis < 3; axis++)
            {
                var moved = Add(Axis(worldBefore, axis), Multiply(new(-maximum, 0), Axis(worldDirection, axis)));
                worldAxes[axis] = Join(worldAxes[axis], Add(moved, Axis(worldError, axis)));
            }
        }
        world = new(worldAxes[0], worldAxes[1], worldAxes[2]);
        return new(domain[0], domain[1], domain[2]);
    }

    static double RecessionScalar(CoverageBoundedVector refined, CoverageBoundedAffine linear,
        CoverageBoundedVector recess, in CoverageCommandPose command)
    {
        var cap = Read(command.MaxWorldRecession);
        var z = Read(new Vector3(command.CoverageWorld.M31, command.CoverageWorld.M32, command.CoverageWorld.M33));
        var zScale = Round32Envelope(Sqrt(Add32(Add32(Mul32(z.X, z.X), Mul32(z.Y, z.Y)), Mul32(z.Z, z.Z))));
        if (!zScale.IsFinite || zScale.Min <= 0) return double.PositiveInfinity;
        // Conform admits F(RN32(full * ZScale)) <= cap. Include multiplication rounding and F.
        var nominal = Divide(Add(cap, Point(FlushLimit)), Multiply(Point(1 - U), zScale));

        // Corrections recompute before - total*Recess and validate the rounded world delta's norm.
        // If B bounds the delta's absolute errors and A its errors per scalar unit, then
        // total * (|Recess*L| - |A|) <= admittedNorm + |B|. All bounds round outward.
        Span<CoverageScalarBounds> baseErrors = stackalloc CoverageScalarBounds[3];
        for (int axis = 0; axis < 3; axis++)
            baseErrors[axis] = Divide(Add(Multiply(Point(U), Point(Abs(Axis(refined, axis)))), Point(3 * FlushLimit)), Point(1 - 3 * U));
        var deltaBase = new CoverageBoundedVector(baseErrors[0], baseErrors[1], baseErrors[2]);
        Span<CoverageScalarBounds> relative = stackalloc CoverageScalarBounds[3];
        Span<CoverageScalarBounds> absolute = stackalloc CoverageScalarBounds[3];
        for (int axis = 0; axis < 3; axis++)
        {
            relative[axis] = Multiply(Gamma(8), ProductSum(recess, linear, axis));
            absolute[axis] = Add(Multiply(Add(One, Gamma(5)), ProductSum(deltaBase, linear, axis)), Point(5 * FlushLimit));
        }
        var relativeNorm = new CoverageBoundedVector(relative[0], relative[1], relative[2]).Length;
        var absoluteNorm = new CoverageBoundedVector(absolute[0], absolute[1], absolute[2]).Length;
        var gain = linear.ApplyLinear(recess).Length;
        var denominator = Subtract(gain, relativeNorm);
        if (!denominator.IsFinite || denominator.Min <= 0) return double.PositiveInfinity;
        // Reverse the final sqrt, three nonnegative squares and two additions, including each F.
        var sqrtLimit = Divide(Add(cap, Point(FlushLimit)), Point(1 - U));
        var norm = Sqrt(Divide(Add(Square(sqrtLimit), Point(5 * FlushLimit)),
            Multiply(Multiply(Point(1 - U), Point(1 - U)), Point(1 - U))));
        var corrected = Divide(Add(norm, absoluteNorm), denominator);
        return Math.Max(nominal.Max, corrected.Max);
    }

    /// <summary>Magnitude guard for the model dot relative to the latched origin. This is not the retired
    /// render-pass certificate and makes no claim about rasterization, depth, equipment or temporal arithmetic.</summary>
    internal double OriginError(in MeshBounds skin, in Matrix4x4 model, Vector3 renderOrigin, ReadOnlySpan<CoverageCommandPose> commands)
    {
        var local = ConformedLocal(skin, model, commands, out _);
        if (!local.IsFinite || !CoverageFrameAdmission.TryActor(model, out _)) return double.PositiveInfinity;
        CoverageScalarBounds residualSquared = Zero;
        double maximumSum = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            if (!float.IsFinite(renderOrigin[axis])) return double.PositiveInfinity;
            var exactTranslation = Subtract(Point(model[3, axis]), Point(renderOrigin[axis]));
            var uploadedTranslation = Round32Envelope(exactTranslation);
            var residual = Point(Abs(Subtract(exactTranslation, uploadedTranslation)));
            var sum = Point(Abs(uploadedTranslation));
            for (int row = 0; row < 3; row++)
            {
                var coefficient = Point(model[row, axis]);
                sum = Add(sum, Point(Abs(Multiply(Axis(local, row), coefficient))));
                // Also enclose a copied coefficient's F if the consuming graph flushes uploaded fields.
                residual = Add(residual, Point(Abs(Multiply(Axis(local, row), Subtract(coefficient, Round32Envelope(coefficient))))));
            }
            residualSquared = Add(residualSquared, Square(residual));
            maximumSum = Math.Max(maximumSum, sum.Max);
        }
        var dotError = Add(Multiply(Gamma(7), Point(maximumSum)), Point(7 * FlushLimit));
        if (!Round32Envelope(Add(Point(maximumSum), dotError)).IsFinite) return double.PositiveInfinity;
        double error = Add(Sqrt(residualSquared), Multiply(Sqrt(Point(3)), dotError)).Max;
        return double.IsFinite(error) ? error : double.PositiveInfinity;
    }

    /// <summary>Runs after identity/capacity/frame admission. The first failing ID in sorted order owns the refusal.</summary>
    internal bool TryAdmitOrigin(in MeshBounds skin, in Matrix4x4 model, Vector3 renderOrigin,
        ReadOnlySpan<CoverageCommandPose> commands, out CoverageFailure failure)
    {
        failure = default;
        if (commands.Length > CoverageScratch.MaxCommands) throw new ArgumentException("Origin admission requires admitted command capacity.", nameof(commands));
        Span<int> order = stackalloc int[CoverageScratch.MaxCommands];
        for (int i = 0; i < commands.Length; i++)
        {
            int j = i;
            while (j > 0 && commands[order[j - 1]].Id > commands[i].Id) { order[j] = order[j - 1]; j--; }
            order[j] = i;
        }
        double error = OriginError(skin, model, renderOrigin, commands);
        foreach (int index in order[..commands.Length])
        {
            var command = commands[index];
            if (Allows(error, command.Clearance)) continue;
            failure = new(CoverageFailureCode.UnsupportedTransform, hostIdentity, command.EquipmentInstanceId, command.Id, 0, 0);
            return false;
        }
        return true;
    }

    internal static bool Allows(double error, float clearance) => double.IsFinite(error) && error >= 0
        && float.IsFinite(clearance) && clearance > 0 && error <= (double)clearance * .25d;

    /// <summary>RN32 followed by F is monotone, including its inclusive flush threshold and signed zero.
    /// Applying it to both endpoints encloses every binary32 result, not merely binary64 evaluation error.</summary>
    internal static CoverageScalarBounds Round32Envelope(CoverageScalarBounds value) => !value.IsFinite ? Invalid
        : new(Flush((float)value.Min), Flush((float)value.Max));

    static float Flush(float value) => (BitConverter.SingleToUInt32Bits(value) & 0x7fffffff) <= 0x00800000
        ? BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(value) & 0x80000000) : value;
    static CoverageScalarBounds Point(double value) => new(value, value);
    static CoverageScalarBounds Read(float value) => Round32Envelope(Point(value));
    static CoverageBoundedVector Read(Vector3 value) => new(Read(value.X), Read(value.Y), Read(value.Z));
    static CoverageScalarBounds Add32(CoverageScalarBounds a, CoverageScalarBounds b) => Round32Envelope(Add(a, b));
    static CoverageScalarBounds Mul32(CoverageScalarBounds a, CoverageScalarBounds b) => Round32Envelope(Multiply(a, b));
    static CoverageScalarBounds Gamma(int n) => Divide(Point(n * U), Point(1 - n * U));
    static CoverageScalarBounds Expand(CoverageScalarBounds value, double error) => Add(value, new(-error, error));
    static double Abs(CoverageScalarBounds value) => Math.Max(Math.Abs(value.Min), Math.Abs(value.Max));
    static CoverageScalarBounds Empty => new(double.PositiveInfinity, double.NegativeInfinity);
    static CoverageBoundedVector EmptyBox => new(Empty, Empty, Empty);
    static CoverageBoundedVector InvalidBox => new(Invalid, Invalid, Invalid);
    static MeshBounds InvalidMesh => new(new(float.NaN), new(float.NaN));
    static CoverageScalarBounds Join(CoverageScalarBounds a, CoverageScalarBounds b) => new(Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max));
    static CoverageBoundedVector Join(CoverageBoundedVector a, CoverageBoundedVector b) => new(Join(a.X, b.X), Join(a.Y, b.Y), Join(a.Z, b.Z));
    static CoverageScalarBounds Axis(CoverageBoundedVector value, int axis) => axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };
    static CoverageBoundedVector FromMesh(in MeshBounds mesh) => new(new(mesh.Min.X, mesh.Max.X), new(mesh.Min.Y, mesh.Max.Y), new(mesh.Min.Z, mesh.Max.Z));
    static MeshBounds ToMesh(CoverageBoundedVector box) => !box.IsFinite ? InvalidMesh : new(
        new(Lower(box.X.Min), Lower(box.Y.Min), Lower(box.Z.Min)), new(Upper(box.X.Max), Upper(box.Y.Max), Upper(box.Z.Max)));
    internal static float Lower(double value) { float f = (float)value; return f > value ? MathF.BitDecrement(f) : f; }
    internal static float Upper(double value) { float f = (float)value; return f < value ? MathF.BitIncrement(f) : f; }
    static CoverageBoundedAffine Matrix(Matrix4x4 m) => new(new(Read(m.M11), Read(m.M12), Read(m.M13)),
        new(Read(m.M21), Read(m.M22), Read(m.M23)), new(Read(m.M31), Read(m.M32), Read(m.M33)), new(Read(m.M41), Read(m.M42), Read(m.M43)));
    static CoverageScalarBounds ProductSum(CoverageBoundedVector box, CoverageBoundedAffine matrix, int axis) =>
        Add(Add(Add(Point(Abs(Multiply(box.X, Axis(matrix.R1, axis)))), Point(Abs(Multiply(box.Y, Axis(matrix.R2, axis))))),
            Point(Abs(Multiply(box.Z, Axis(matrix.R3, axis))))), Point(Abs(Axis(matrix.Translation, axis))));
    static CoverageScalarBounds FieldError(CoverageBoundedVector box, Matrix4x4 model, int axis)
    {
        var error = Point(Abs(Subtract(Point(model[3, axis]), Read(model[3, axis]))));
        for (int row = 0; row < 3; row++)
            error = Add(error, Point(Abs(Multiply(Axis(box, row), Subtract(Point(model[row, axis]), Read(model[row, axis]))))));
        return error;
    }
    static double MaxRecession(ReadOnlySpan<CoverageCommandPose> commands)
    {
        double maximum = 0;
        foreach (var command in commands)
        {
            if (!float.IsFinite(command.MaxWorldRecession) || command.MaxWorldRecession < 0) return double.PositiveInfinity;
            maximum = Math.Max(maximum, command.MaxWorldRecession);
        }
        return maximum;
    }
}
