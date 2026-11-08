using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class AttachmentJsonPath
{
    internal static string Property(string parent, string name)
    {
        bool simple = name.Length > 0 && name[0] is (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_');
        foreach (char character in name)
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            {
                simple = false;
                break;
            }
        return simple ? parent + "." + name : parent + "[\"" + JsonEncodedText.Encode(name) + "\"]";
    }
}
