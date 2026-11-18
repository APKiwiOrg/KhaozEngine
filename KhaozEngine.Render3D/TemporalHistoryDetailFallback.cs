namespace KhaozEngine.Render3D;

/// <summary>Why the scene uses Display history when Fine was requested.</summary>
public enum TemporalHistoryDetailFallback
{
    /// <summary>The requested grid is in effect, or Display was requested.</summary>
    None,
    /// <summary>The frame runs no temporal resolve.</summary>
    NoResolve,
    /// <summary>The device has fewer than six colour attachments.</summary>
    ColorAttachments,
    /// <summary>The display exceeds <see cref="TemporalSettings.HistoryDetailPixelLimit"/>, or the limit is not positive.</summary>
    PixelBudget,
    /// <summary>Twice the display exceeds the device's 2D render texture dimension, or the limit is unknown.</summary>
    TextureSize,
    /// <summary>Fine target creation failed. Display holds until the display size changes, Display is requested
    /// for a frame or the temporal resolve stops.</summary>
    CreationFailed,
}
