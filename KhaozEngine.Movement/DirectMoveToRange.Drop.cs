using System.Numerics;
using KhaozEngine.Locomotion;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

public sealed partial class DirectMoveToRange
{
    /// <summary>The most zero-input steps a predicted fall may take to land before the drop is refused.</summary>
    internal const int MaxDropSettleSteps = 256;

    /// <summary>Admits a predicted step that leaves the ground under <see cref="DirectApproachOptions.MaxDropMetres"/>.
    /// The fall settles on a copy through the live context with zero input, as the Suspended ticks after it will. It is
    /// admitted when the body lands grounded and dry with its feet no lower than the allowance below the current feet.
    /// A fall that starts swimming, sinks past the allowance, turns non-finite or has not landed within
    /// <see cref="MaxDropSettleSteps"/> steps is refused. A zero allowance refuses without stepping.</summary>
    private bool AllowsDrop(in MoveState body, in MoveState predicted, in MoveTuning tuning, float dt,
        GroundMoveContext context)
    {
        float allowance = _options.MaxDropMetres;
        if (allowance == 0f || predicted.Grounded || predicted.Swimming) return false;
        float lowest = Feet(body, tuning).Y - allowance;
        MoveState fall = predicted;
        for (int step = 0; step < MaxDropSettleSteps; step++)
        {
            if (!MovementBody.IsFinite(fall.Position) || fall.Swimming || Feet(fall, tuning).Y < lowest) return false;
            if (fall.Grounded) return true;
            fall = context.Step(fall, Vector2.Zero, false, dt, tuning);
        }
        return false;
    }
}
