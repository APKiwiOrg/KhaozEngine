namespace KhaozEngine.Attachments;

internal static class AttachmentText
{
    internal static bool IsWellFormedUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (++index == value.Length || !char.IsLowSurrogate(value[index]))
                    return false;
            }
            else if (char.IsLowSurrogate(current))
                return false;
        }
        return true;
    }
}
