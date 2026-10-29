namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>Distinguishes publication, a temporary ownership gap and confirmed disappearance.</summary>
public enum StatusEffectPublishResult : byte
{
    Published,
    Deferred,
    Gone
}
