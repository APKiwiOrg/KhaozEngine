using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace GestureInputProbe;

internal static class Program
{
    static void Main()
    {
        using var window = new AppWindow("Gesture input probe", 1100, 800);
        using var surface = new Render2DSurface(window);
        using var white = Render2DTextures.White(surface);
        using var font = surface.LoadDefaultDpiFont(14);
        var scene = new ProbeScene(window, surface, white, font);
        window.Run(scene.Draw, scene.Prepare);
    }
}
