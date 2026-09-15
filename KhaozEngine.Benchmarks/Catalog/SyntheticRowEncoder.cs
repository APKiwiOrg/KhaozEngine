using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// Encodes one synthetic row of any registered type into a caller supplied span. The edit set is how the
/// P5 and P6 one-item edit changes bytes: an edited item's <c>value</c> moves, which rewrites exactly the
/// one chunk that holds it (section 6.6).
/// </summary>
public sealed class SyntheticRowEncoder
{
    private readonly SyntheticContentSet _content;
    private ItemRowData _item;
    private TagRowData _tag;
    private StatRowData _stat;
    private LootTableRowData _lootTable;
    private LootEntryRowData _lootEntry;
    private BaseSocketRowData _baseSocket;
    private GameRowData _gameRow;

    public SyntheticRowEncoder(SyntheticContentSet content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
    }

    /// <summary>Item ids whose row differs from the base generation, the edit of budgets P5 and P6.</summary>
    public HashSet<int>? EditedItemIds { get; set; }

    /// <summary>Bytes the content KEY cost across every row this encoder wrote, reported separately.</summary>
    public long KeyBytes { get; private set; }

    /// <summary>
    /// A row body is the content KEY, length prefixed UTF-8, then the schema fields positionally. The key
    /// is in the row because the runtime of section 9.1 holds a <c>Keys</c> array and section 9.4 derives a
    /// key to id index at load, and the pack carries nothing else it could come from. Section 14.1's row
    /// arithmetic leaves it out, so the measurement reports the key bytes separately as well.
    /// </summary>
    public int Encode(ushort typeId, int id, Span<byte> destination)
    {
        string key = _content.KeyFor(typeId, id);
        int prefix = ContentVarint.Write(destination, (uint)key.Length);
        prefix += Encoding.UTF8.GetBytes(key, destination[prefix..]);
        KeyBytes += prefix;
        return prefix + EncodeFields(typeId, id, destination[prefix..]);
    }

    private int EncodeFields(ushort typeId, int id, Span<byte> destination)
    {
        switch (typeId)
        {
            case ContentTypes.Tag:
                _content.Tag(id, ref _tag);
                return ContentRowCodec.EncodeTag(in _tag, destination);
            case ContentTypes.Item:
                _content.Item(id, ref _item);
                if (EditedItemIds is not null && EditedItemIds.Contains(id)) _item.Value = (_item.Value % 250_000) + 1;
                return ContentRowCodec.EncodeItem(in _item, destination);
            case ContentTypes.Stat:
                _content.Stat(id, ref _stat);
                return ContentRowCodec.EncodeStat(in _stat, destination);
            case ContentTypes.LootTable:
                _content.LootTable(id, ref _lootTable);
                return ContentRowCodec.EncodeLootTable(in _lootTable, destination);
            case ContentTypes.LootEntry:
                _content.LootEntry(id, ref _lootEntry);
                return ContentRowCodec.EncodeLootEntry(in _lootEntry, destination);
            case ContentTypes.BaseSocket:
                _content.BaseSocket(id, ref _baseSocket);
                return ContentRowCodec.EncodeBaseSocket(in _baseSocket, destination);
            default:
                _content.GameRow(typeId, id, ref _gameRow);
                return ContentRowCodec.EncodeGame(in _gameRow, destination);
        }
    }

    public bool IsRetired(ushort typeId, int id) => _content.IsRetired(typeId, id);
}
