namespace KhaozEngine.Movement;

/// <summary>The ground context capability rule that refused contact movement.</summary>
public enum GroundContactCapabilityRefusal
{
    /// <summary>The producer restricted this context to legacy movement.</summary>
    ContactProducerRestricted,
    /// <summary>Contact movement requires an ordinary-constructed ground context.</summary>
    OrdinaryContextRequired,
    /// <summary>Contact prediction requires an ordinary contact binding.</summary>
    OrdinaryPredictionRequired,
}
