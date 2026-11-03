using System;

namespace KhaozEngine.MapEditor;

public sealed partial class ViewportWorld
{
    private sealed class PickingScopeForTests(Action restore) : IDisposable
    {
        private Action? restore = restore;

        public void Dispose()
        {
            Action? action = restore;
            restore = null;
            action?.Invoke();
        }
    }
}
