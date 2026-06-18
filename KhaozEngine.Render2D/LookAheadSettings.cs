using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Look-ahead configuration: lead the camera ahead of the target along its velocity. Per frame the lead
    /// target is <c>clamp(velocity * LeadTime, -MaxDistance .. +MaxDistance)</c> per axis; the applied offset
    /// eases toward that target at <see cref="Stiffness"/> so a direction reversal does not snap. The
    /// <c>default</c> value (all zero) is disabled — <see cref="LeadTime"/> of 0 on an axis means no lead.
    /// </summary>
    public readonly struct LookAheadSettings
    {
        /// <summary>Seconds of velocity to lead by, per axis. 0 on an axis = no lead there.</summary>
        public readonly Vector2 LeadTime;

        /// <summary>Clamp on lead magnitude, per axis (world units). A component &lt;= 0 = unclamped.</summary>
        public readonly Vector2 MaxDistance;

        /// <summary>Easing rate of the lead offset (per second). &lt;= 0 = apply instantly.</summary>
        public readonly float Stiffness;

        /// <summary>Creates look-ahead settings.</summary>
        public LookAheadSettings(Vector2 leadTime, Vector2 maxDistance, float stiffness)
        {
            LeadTime = leadTime;
            MaxDistance = maxDistance;
            Stiffness = stiffness;
        }
    }
}
