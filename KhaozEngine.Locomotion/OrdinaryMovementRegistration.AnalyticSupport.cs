using System;

namespace KhaozEngine.Locomotion;

public sealed partial class OrdinaryMovementRegistration
{
    /// <summary>Optional immutable analytic provenance captured by the installer. Ordinary support selection does not depend on it.</summary>
    public IMovementAnalyticSupportSource? AnalyticSupport { get; }

    public sealed partial class Builder
    {
        internal IMovementAnalyticSupportSource? AnalyticSupport { get; private set; }

        /// <summary>Attaches the source of the exact analytic height and normal delegates. Identity consumers must
        /// verify Matches before using it. The registration still authenticates the installed physical generation.</summary>
        public Builder WithAnalyticSupport(IMovementAnalyticSupportSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            AnalyticSupport = source;
            return this;
        }
    }
}
