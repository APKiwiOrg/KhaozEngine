namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>Publishes an actor's shown effects to its replicated state.</summary>
public interface IStatusEffectProjectionTarget
{
    /// <summary>Reports whether the state was published, must wait for ownership or the actor is gone.</summary>
    StatusEffectPublishResult Publish(long actor, in ActiveStatusEffects state);
}
