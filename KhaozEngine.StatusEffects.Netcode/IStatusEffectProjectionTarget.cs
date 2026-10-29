namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>Publishes an actor's shown effects to its replicated state.</summary>
public interface IStatusEffectProjectionTarget
{
    /// <summary>Returns false when the actor no longer exists.</summary>
    bool TryPublish(long actor, in ActiveStatusEffects state);
}
