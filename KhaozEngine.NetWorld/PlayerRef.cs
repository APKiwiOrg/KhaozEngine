namespace KhaozEngine.NetWorld;

/// <summary>An admin command target: either a connection slot or a verified account id. Build with
/// <see cref="Slot"/> / <see cref="Account"/>; the server resolves it to a slot on the host thread.</summary>
public readonly struct PlayerRef
{
    private PlayerRef(bool isSlot, int slot, string account) { IsSlot = isSlot; SlotValue = slot; AccountValue = account; }

    public static PlayerRef Slot(int slot) => new(true, slot, string.Empty);
    public static PlayerRef Account(string accountId) => new(false, 0, accountId ?? string.Empty);

    public bool IsSlot { get; }
    public int SlotValue { get; }
    public string AccountValue { get; }
}
