namespace KhaozEngine.Gui
{
    /// <summary>
    /// The layer a <see cref="WindowStack"/> window lives in, bottom to top. No window ever rises above a higher
    /// band, and within a band the stack keeps its own order.
    /// </summary>
    public enum WindowBand
    {
        /// <summary>Panels docked to the screen edge, below every floating window.</summary>
        Docked,
        /// <summary>Ordinary free-floating windows.</summary>
        Floating,
        /// <summary>Windows that stay above every floating window, such as a debug menu.</summary>
        Topmost,
    }
}
