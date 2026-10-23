using System;

namespace KhaozEngine.Gpu;

public partial interface IGpuDevice
{
    /// <summary>Submit once and signal each completion fence at the same completion point.</summary>
    void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
    {
        GpuCompletionFences.Validate(completions);
        if (completions.IsEmpty) Submit(commands);
        else if (completions.Length == 1) Submit(commands, completions[0]);
        else throw new NotSupportedException("This device does not support several completion fences per submission.");
    }
}

internal static class GpuCompletionFences
{
    internal static void Validate(ReadOnlySpan<IGpuFence> completions)
    {
        for (int i = 0; i < completions.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(completions[i]);
            for (int j = 0; j < i; j++)
                if (ReferenceEquals(completions[i], completions[j]))
                    throw new ArgumentException("A completion fence was repeated.", nameof(completions));
        }
    }
}
