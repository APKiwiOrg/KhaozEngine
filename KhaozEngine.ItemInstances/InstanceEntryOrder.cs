namespace KhaozEngine.ItemInstances;

/// <summary>The structural ordering rule for a field's repeating entries.</summary>
public enum InstanceEntryOrder : byte
{
    /// <summary>Keep entries in the sequence their author supplied.</summary>
    Authored = 0,

    /// <summary>
    /// Restore ascending unsigned order by the sole entry reference target's scalar slot. Equal keys
    /// retain their authored order. The registered codec decides whether duplicate keys are valid.
    /// </summary>
    AscendingByEntryReference = 1,
}
