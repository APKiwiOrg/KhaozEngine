using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Attempt-owned bounded pages. Growth admits both directories before the old one retires.</summary>
internal sealed class MapMembershipArenaPages<T> where T : struct
{
    readonly int _capacity;
    int _count, _pageCount;
    bool _frozen;
    long _bytes = 48;
    T[][] _pages = Array.Empty<T[]>();
    MapMembershipArenaPages(int capacity) => _capacity = capacity;
    internal static MapMembershipArenaPages<T> Create(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        MapMembershipBuild.Inventory(48);
        return new(capacity);
    }
    internal int Count => _count;
    internal int PageCount => _pageCount;
    internal int Capacity => checked(_pageCount * _capacity);
    internal long InventoryBytes => _bytes;
    internal static long ArrayBytes(int capacity) => checked((24L + (long)capacity * Unsafe.SizeOf<T>() + 7) & ~7L);
    internal ref readonly T this[int index] => ref _pages[index / _capacity][index % _capacity];
    internal int Append(T value)
    {
        int index = Allocate(1);
        _pages[index / _capacity][index % _capacity] = value;
        return index;
    }
    internal int Allocate(int count)
    {
        if (_frozen) throw new InvalidOperationException("Membership pages are frozen.");
        if (count <= 0 || count > _capacity) throw new ArgumentOutOfRangeException(nameof(count));
        int index = _count;
        if (index % _capacity + count > _capacity) index = checked((index / _capacity + 1) * _capacity);
        if (index / _capacity == _pageCount) AddPage();
        _count = checked(index + count);
        return index;
    }
    internal Span<T> Writable(int index, int count)
    {
        if (_frozen) throw new InvalidOperationException("Membership pages are frozen.");
        return _pages[index / _capacity].AsSpan(index % _capacity, count);
    }
    internal ReadOnlySpan<T> Read(int index, int count) => _pages[index / _capacity].AsSpan(index % _capacity, count);
    void AddPage()
    {
        if (_pageCount == _pages.Length)
        {
            int capacity = _pages.Length == 0 ? 1 : checked(2 * _pages.Length);
            long bytes = checked(24L + 8L * capacity), oldBytes = _pages.Length == 0 ? 0 : 24L + 8L * _pages.Length;
            MapMembershipBuild.Inventory(bytes);
            var replacement = new T[capacity][];
            Array.Copy(_pages, replacement, _pageCount);
            _pages = replacement;
            _bytes = checked(_bytes + bytes - oldBytes);
            MapMembershipBuild.ReleaseInventory(oldBytes);
        }
        long pageBytes = ArrayBytes(_capacity);
        MapMembershipBuild.Inventory(pageBytes);
        _pages[_pageCount++] = new T[_capacity];
        _bytes = checked(_bytes + pageBytes);
    }
    internal void Freeze() => _frozen = true;
    internal IEnumerable<(object Identity, long Bytes)> Inventory()
    {
        yield return (this, 48);
        if (_pages.Length != 0) yield return (_pages, 24L + 8L * _pages.Length);
        for (int i = 0; i < _pageCount; i++) yield return (_pages[i], ArrayBytes(_capacity));
    }
}
