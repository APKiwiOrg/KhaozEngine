using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

public partial class MapEditorScene
{
    void UpdateGuiInput(UiViewport viewport)
    {
        _ui.Update(Manager!.Input, viewport);
        if (NavigationOwnsPointer) _ui.SuppressPointerInput();
    }
}
