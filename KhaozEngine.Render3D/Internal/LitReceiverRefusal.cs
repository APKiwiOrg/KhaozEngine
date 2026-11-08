namespace KhaozEngine.Render3D;

/// <summary>The stable reasons LitReceiver input or state is refused.</summary>
public enum LitReceiverRefusal
{
    /// <summary>Refuses a device below the lit-receiver fragment storage-buffer requirement.</summary>
    FragmentStorageBufferLimitInsufficient,
}
