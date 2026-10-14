using System;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

[Collection("AllocSensitive")]
public sealed class NameplateContentTests
{
    [Fact]
    public void TextCanBeSetAndRead()
    {
        var content = new NameplateContent();
        var tint = new Color(0.4f, 0.5f, 0.6f);

        content.SetText(NameplateSlot.Name, "Zombie", Color.White, tint);

        Assert.True(content.TryGetText(NameplateSlot.Name, out var value));
        Assert.Equal(new NameplateTextValue("Zombie", Color.White, tint), value);
    }

    [Fact]
    public void BarCanBeSetAndRead()
    {
        var content = new NameplateContent();

        content.SetBar(NameplateSlot.Health, 0.75f, Color.White, Color.Transparent, Color.Black);

        Assert.True(content.TryGetBar(NameplateSlot.Health, out var value));
        Assert.Equal(new NameplateBarValue(0.75f, Color.White, Color.Transparent, Color.Black), value);
    }

    [Fact]
    public void OptionalColorsDefaultToNull()
    {
        var content = new NameplateContent();

        content.SetText(NameplateSlot.Name, "Zombie", Color.White);
        content.SetBar(NameplateSlot.Health, 0.5f, Color.White, Color.Black);

        Assert.True(content.TryGetText(NameplateSlot.Name, out var text));
        Assert.Null(text.Tint);
        Assert.True(content.TryGetBar(NameplateSlot.Health, out var bar));
        Assert.Null(bar.Border);
    }

    [Fact]
    public void ASecondTextSetReplacesAllValues()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Zombie", Color.White, Color.Black);

        content.SetText(NameplateSlot.Name, "Hardened zombie", Color.Black);

        Assert.True(content.TryGetText(NameplateSlot.Name, out var value));
        Assert.Equal(new NameplateTextValue("Hardened zombie", Color.Black, null), value);
    }

    [Fact]
    public void ASecondBarSetReplacesAllValues()
    {
        var content = new NameplateContent();
        content.SetBar(NameplateSlot.Health, 0.75f, Color.White, Color.Transparent, Color.Black);

        content.SetBar(NameplateSlot.Health, 0.25f, Color.Black, Color.White);

        Assert.True(content.TryGetBar(NameplateSlot.Health, out var value));
        Assert.Equal(new NameplateBarValue(0.25f, Color.Black, Color.White, null), value);
    }

    [Fact]
    public void ClearEmptiesEverySlotAndResetsOpacity()
    {
        var content = new NameplateContent();
        Assert.Equal(1f, content.Opacity);
        content.SetText(NameplateSlot.Name, "Zombie", Color.White);
        content.SetText(NameplateSlot.Level, "9", Color.White);
        content.SetBar(NameplateSlot.Health, 0.5f, Color.White, Color.Black);
        content.Opacity = 0.4f;

        content.Clear();

        Assert.False(content.TryGetText(NameplateSlot.Name, out _));
        Assert.False(content.TryGetText(NameplateSlot.Level, out _));
        Assert.False(content.TryGetBar(NameplateSlot.Health, out _));
        Assert.Equal(1f, content.Opacity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyTextLeavesTheSlotEmpty(string? text)
    {
        var content = new NameplateContent();

        content.SetText(NameplateSlot.Name, text, Color.White);

        Assert.False(content.TryGetText(NameplateSlot.Name, out _));
        Assert.False(content.TryGetBar(NameplateSlot.Name, out _));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void EmptyTextRemovesAnExistingSlotWithoutLosingOtherSlots(string? text, bool wasBar)
    {
        var content = new NameplateContent();
        if (wasBar)
            content.SetBar(NameplateSlot.Name, 0.5f, Color.White, Color.Black);
        else
            content.SetText(NameplateSlot.Name, "Zombie", Color.White);
        content.SetText(NameplateSlot.Level, "9", Color.White);
        content.SetBar(NameplateSlot.Health, 0.75f, Color.White, Color.Black);

        content.SetText(NameplateSlot.Name, text, Color.White);

        Assert.False(content.TryGetText(NameplateSlot.Name, out _));
        Assert.False(content.TryGetBar(NameplateSlot.Name, out _));
        Assert.True(content.TryGetText(NameplateSlot.Level, out var level));
        Assert.Equal("9", level.Text);
        Assert.True(content.TryGetBar(NameplateSlot.Health, out var health));
        Assert.Equal(0.75f, health.Fraction);
    }

    [Fact]
    public void TextAndBarSlotsCannotBeReadAsTheOtherKind()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Zombie", Color.White);
        content.SetBar(NameplateSlot.Health, 0.5f, Color.White, Color.Black);

        Assert.False(content.TryGetBar(NameplateSlot.Name, out var bar));
        Assert.Equal(default, bar);
        Assert.False(content.TryGetText(NameplateSlot.Health, out var text));
        Assert.Equal(default, text);
    }

    [Fact]
    public void SettingTheOtherKindReplacesTheSlot()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Zombie", Color.White);

        content.SetBar(NameplateSlot.Name, 0.5f, Color.White, Color.Black);

        Assert.False(content.TryGetText(NameplateSlot.Name, out _));
        Assert.True(content.TryGetBar(NameplateSlot.Name, out var bar));
        Assert.Equal(0.5f, bar.Fraction);

        content.SetText(NameplateSlot.Name, "Zombie", Color.Black);

        Assert.False(content.TryGetBar(NameplateSlot.Name, out _));
        Assert.True(content.TryGetText(NameplateSlot.Name, out var text));
        Assert.Equal(new NameplateTextValue("Zombie", Color.Black, null), text);
    }

    [Fact]
    public void SlotsWithTheSameKeyReferToTheSameValue()
    {
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Level, "9", Color.White);

        content.SetText(new NameplateSlot("level"), "12", Color.Black);

        Assert.True(content.TryGetText(NameplateSlot.Level, out var value));
        Assert.Equal(new NameplateTextValue("12", Color.Black, null), value);
    }

    [Fact]
    public void ContentGrowsAndCanBeRefilledAfterClear()
    {
        var content = new NameplateContent();
        var slots = new NameplateSlot[24];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new NameplateSlot($"slot-{i}");
            content.SetText(slots[i], "Zombie", Color.White);
        }

        foreach (var slot in slots)
        {
            Assert.True(content.TryGetText(slot, out var value));
            Assert.Equal("Zombie", value.Text);
        }

        content.Clear();
        foreach (var slot in slots)
        {
            Assert.False(content.TryGetText(slot, out _));
            content.SetBar(slot, 0.5f, Color.White, Color.Black);
        }

        foreach (var slot in slots)
        {
            Assert.True(content.TryGetBar(slot, out var value));
            Assert.Equal(new NameplateBarValue(0.5f, Color.White, Color.Black, null), value);
            Assert.False(content.TryGetText(slot, out _));
        }
    }

    [Fact]
    public void AWarmContentAllocatesNothing()
    {
        var content = new NameplateContent();
        for (int i = 0; i < 24; i++)
            content.SetText(new NameplateSlot($"warm-{i}"), "Zombie", Color.White);
        content.Clear();
        Assert.True(SetAndGet(content));
        GC.GetAllocatedBytesForCurrentThread();

        long before, after;
        bool found;
        using (AllocationRegion.Start("a warm content set and get"))
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            content.Clear();
            found = SetAndGet(content);
            after = GC.GetAllocatedBytesForCurrentThread();
        }

        Assert.True(found);
        Assert.Equal(before, after);

        static bool SetAndGet(NameplateContent content)
        {
            content.SetText(NameplateSlot.Name, "Zombie", Color.White);
            content.SetText(NameplateSlot.Level, "9", Color.White, Color.Black);
            content.SetBar(NameplateSlot.Health, 0.75f, Color.White, Color.Black, Color.White);
            bool name = content.TryGetText(NameplateSlot.Name, out var nameValue);
            bool level = content.TryGetText(NameplateSlot.Level, out var levelValue);
            bool health = content.TryGetBar(NameplateSlot.Health, out var healthValue);
            return name && level && health && nameValue.Text == "Zombie" &&
                levelValue.Text == "9" && healthValue.Fraction == 0.75f;
        }
    }
}
