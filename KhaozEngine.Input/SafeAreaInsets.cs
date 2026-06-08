namespace KhaozEngine.Input;

// Insets in virtual pixels for notches/cutouts/system UI. Launchers set these.
public readonly record struct SafeAreaInsets(float Top, float Bottom, float Left, float Right)
{
    public static readonly SafeAreaInsets Zero = new(0, 0, 0, 0);
}
