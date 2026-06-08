namespace KhaozEngine.Input;

// The seam that makes input testable: production reads hardware, tests inject snapshots.
public interface IRawInput
{
    RawInputState Read();
}
