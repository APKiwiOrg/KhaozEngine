using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>Reads a catalog tag-list value into reusable stat construction storage.</summary>
static class ContentTagListReader
{
    /// <summary>Replaces <paramref name="destination"/> with every readable positive tag id.</summary>
    public static void Read(in ContentFieldValue value, List<int> destination)
    {
        destination.Clear();
        if (value.IsAbsent || value.Bytes.IsEmpty)
        {
            return;
        }

        int maximum = value.Bytes.Length;
        destination.EnsureCapacity(maximum);
        CollectionsMarshal.SetCount(destination, maximum);
        int count = ContentRowCodecBase.ReadTagList(in value, CollectionsMarshal.AsSpan(destination));
        CollectionsMarshal.SetCount(destination, count);
    }
}
