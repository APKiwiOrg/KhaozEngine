using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>The text, foreground colour and optional badge tint for a slot.</summary>
public readonly record struct NameplateTextValue(string Text, Color Color, Color? Tint);

/// <summary>The fill fraction and colours for a bar slot.</summary>
public readonly record struct NameplateBarValue(float Fraction, Color Fill, Color Track, Color? Border);

/// <summary>Reusable nameplate values. Setting and reading warmed slots does not allocate.</summary>
public sealed class NameplateContent
{
    private Entry[] _entries = new Entry[8];
    private int _count;

    public float Opacity { get; set; } = 1f;

    /// <summary>Empties every slot and restores full opacity, retaining storage for the next fill.</summary>
    public void Clear()
    {
        Array.Clear(_entries, 0, _count);
        _count = 0;
        Opacity = 1f;
    }

    /// <summary>Replaces a slot with text. Null or empty text removes its current value.</summary>
    public void SetText(NameplateSlot slot, string? text, Color color, Color? tint = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            Remove(slot);
            return;
        }

        int index = GetOrAddIndex(slot);
        _entries[index] = new Entry
        {
            Slot = slot,
            Text = new NameplateTextValue(text, color, tint),
            IsText = true,
        };
    }

    /// <summary>Replaces a slot with a bar value.</summary>
    public void SetBar(NameplateSlot slot, float fraction, Color fill, Color track, Color? border = null)
    {
        int index = GetOrAddIndex(slot);
        _entries[index] = new Entry
        {
            Slot = slot,
            Bar = new NameplateBarValue(fraction, fill, track, border),
        };
    }

    public bool TryGetText(NameplateSlot slot, out NameplateTextValue value)
    {
        int index = Find(slot);
        if (index >= 0 && _entries[index].IsText)
        {
            value = _entries[index].Text;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetBar(NameplateSlot slot, out NameplateBarValue value)
    {
        int index = Find(slot);
        if (index >= 0 && !_entries[index].IsText)
        {
            value = _entries[index].Bar;
            return true;
        }

        value = default;
        return false;
    }

    private int Find(NameplateSlot slot)
    {
        for (int i = 0; i < _count; i++)
            if (_entries[i].Slot == slot)
                return i;
        return -1;
    }

    private int GetOrAddIndex(NameplateSlot slot)
    {
        int index = Find(slot);
        if (index >= 0)
            return index;

        if (_count == _entries.Length)
            Array.Resize(ref _entries, _entries.Length * 2);
        return _count++;
    }

    private void Remove(NameplateSlot slot)
    {
        int index = Find(slot);
        if (index < 0)
            return;

        _count--;
        _entries[index] = _entries[_count];
        _entries[_count] = default;
    }

    private struct Entry
    {
        public NameplateSlot Slot;
        public NameplateTextValue Text;
        public NameplateBarValue Bar;
        public bool IsText;
    }
}
