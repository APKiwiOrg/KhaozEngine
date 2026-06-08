namespace KhaozEngine.Input;

/// <summary>
/// Insets in virtual pixels for areas obscured by notches, rounded corners, or system UI.
/// Platform launchers set these; layout code keeps interactive content inside them.
/// </summary>
/// <param name="Top">Inset from the top edge.</param>
/// <param name="Bottom">Inset from the bottom edge.</param>
/// <param name="Left">Inset from the left edge.</param>
/// <param name="Right">Inset from the right edge.</param>
public readonly record struct SafeAreaInsets(float Top, float Bottom, float Left, float Right)
{
    /// <summary>No insets (all zero).</summary>
    public static readonly SafeAreaInsets Zero = new(0, 0, 0, 0);
}
