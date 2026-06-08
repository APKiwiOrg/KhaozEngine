namespace KhaozEngine.Screens;

// ConsumeWhenVisible: the top visible interactive screen occupies input regardless
//   of what it did (Hardpoint/Nullwake).
// ConsumeWhenHandled: the screen blocks lower screens only if it reports it handled
//   input this frame (SpaceGame's TryHandleInput-returns-bool).
public enum InputConsumption { ConsumeWhenVisible, ConsumeWhenHandled }
