using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A non-modal window a <see cref="WindowStack"/> orders, updates and draws. A closed window stays registered
    /// and is skipped by update, draw, <see cref="WindowStack.Covers"/> and <see cref="WindowStack.CloseFront"/>.
    /// </summary>
    public interface IStackWindow
    {
        /// <summary>Whether the window is showing. The stack raises a window on the frame this becomes true.</summary>
        bool IsOpen { get; }

        /// <summary>Where the window sits this frame, in the pointer's space. Reserved with
        /// <see cref="Pointer.BlockRegion"/> once the window has updated.</summary>
        Rect Bounds { get; }

        /// <summary>True while the window holds the pointer, normally because a context menu it owns is standing.
        /// A capturing window updates first with nothing occluded and every other window sees the whole surface
        /// occluded.</summary>
        bool CapturesPointer => false;

        /// <summary>Whether <see cref="WindowStack.CloseFront"/> may close this window.</summary>
        bool ClosesOnEscape => true;

        /// <summary>Whether <paramref name="point"/> lies on the window. Override for a window that is not one
        /// rectangle, together with <see cref="Occlude"/>.</summary>
        bool Contains(Vector2 point) => Bounds.Contains(point);

        /// <summary>Occlude the window's area on <paramref name="pointer"/> for the windows below it.</summary>
        void Occlude(Pointer pointer) => pointer.Occlude(Bounds);

        /// <summary>Close the window, as Escape would.</summary>
        void Close();

        /// <summary>Update the window with every window above it occluded on <see cref="WindowUpdate.Pointer"/>.</summary>
        void Update(in WindowUpdate update);

        /// <summary>Draw the window. <paramref name="pointer"/> carries the occluders of the windows above it, so
        /// hover reads agree with what the update saw.</summary>
        void Draw(SpriteBatch batch, Pointer pointer);
    }
}
