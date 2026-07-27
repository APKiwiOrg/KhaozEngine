using System.Numerics;

namespace KhaozEngine.MapEditor;


/// <summary>Per-frame editor input, GPU-free and immutable: the pick ray (origin plus a caller-normalized
/// direction, so a returned pick T reads as a world distance), the pointer press/down/release edges, the
/// screen-space distance the pointer has travelled since the press (for the body-drag arming threshold), the shift
/// modifier, the delete/escape key edges, and the frame delta. A scene wires the window input into this struct,
/// and the controller reads nothing else, so its whole policy is headless-testable frame by frame.</summary>
public readonly struct EditorFrameInput
{
    /// <summary>World-space pick ray origin (the camera eye).</summary>
    public Vector3 RayOrigin { get; }
    /// <summary>World-space pick ray direction, normalized by the caller so pick T reads as a world distance.</summary>
    public Vector3 RayDirection { get; }
    /// <summary>True on the frame the primary pointer button went down (press edge).</summary>
    public bool PointerPressed { get; }
    /// <summary>True while the primary pointer button is held.</summary>
    public bool PointerDown { get; }
    /// <summary>True on the frame the primary pointer button went up (release edge).</summary>
    public bool PointerReleased { get; }
    /// <summary>Screen-space distance (design units, the space the pointer helpers work in) from the press origin to
    /// the current pointer position, i.e. how far the pointer has moved since the button went down. Zero on the
    /// press frame. The body-drag gesture arms only once this clears
    /// <see cref="EditorToolController.BodyDragThreshold"/>, matching the TreeView row-drag threshold precedent, so
    /// a tap below it never turns into a move.</summary>
    public float PointerTravel { get; }
    /// <summary>True while a shift modifier is held (switches the draw modes from disc to rect).</summary>
    public bool Shift { get; }
    /// <summary>True on the frame the delete key went down (removes the selection).</summary>
    public bool DeletePressed { get; }
    /// <summary>True on the frame the escape key went down (cancels the gesture and returns to Select).</summary>
    public bool EscapePressed { get; }
    /// <summary>Seconds elapsed this frame.</summary>
    public float Dt { get; }

    /// <summary>Builds a frame input. Every flag defaults to false, <paramref name="pointerTravel"/> and
    /// <paramref name="dt"/> to zero, so a test only names the edges it exercises.</summary>
    public EditorFrameInput(Vector3 rayOrigin, Vector3 rayDirection,
        bool pointerPressed = false, bool pointerDown = false, bool pointerReleased = false,
        float pointerTravel = 0f,
        bool shift = false, bool deletePressed = false, bool escapePressed = false, float dt = 0f)
    {
        RayOrigin = rayOrigin;
        RayDirection = rayDirection;
        PointerPressed = pointerPressed;
        PointerDown = pointerDown;
        PointerReleased = pointerReleased;
        PointerTravel = pointerTravel;
        Shift = shift;
        DeletePressed = deletePressed;
        EscapePressed = escapePressed;
        Dt = dt;
    }
}
