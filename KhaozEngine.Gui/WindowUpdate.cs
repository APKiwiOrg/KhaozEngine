using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// What a <see cref="WindowStack"/> hands one window for one frame's update.
    /// </summary>
    public readonly struct WindowUpdate
    {
        /// <summary>Build the frame's update for one window.</summary>
        public WindowUpdate(Pointer pointer, InputState input, float dt, bool receivesInput, bool isFront)
        {
            Pointer = pointer;
            Input = input;
            Dt = dt;
            ReceivesInput = receivesInput;
            IsFront = isFront;
        }

        /// <summary>The frame's pointer, with every window above this one occluded.</summary>
        public Pointer Pointer { get; }

        /// <summary>The frame's input. Read the keyboard only while <see cref="ReceivesInput"/> is true.</summary>
        public InputState Input { get; }

        /// <summary>Elapsed seconds since the last frame.</summary>
        public float Dt { get; }

        /// <summary>False on a frame the stack was told it receives no input. The pointer is then wholly occluded
        /// and the window must not read keyboard input, the same contract as <see cref="Screen.Update"/>.</summary>
        public bool ReceivesInput { get; }

        /// <summary>True for the top-most open window of its band, the one keyboard focus normally belongs to.</summary>
        public bool IsFront { get; }
    }
}
