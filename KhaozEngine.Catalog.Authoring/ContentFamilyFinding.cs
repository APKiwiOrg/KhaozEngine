using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>The rules <see cref="ContentFamily"/> checks, in the order it checks them.</summary>
internal enum ContentFamilyRule
{
    /// <summary>The family key is empty or white space.</summary>
    KeyBlank,

    /// <summary>The declared block size is outside <see cref="ContentFamily.MinBlockSize"/> to <see cref="ContentFamily.MaxBlockSize"/>.</summary>
    BlockSizeOutsideRange,

    /// <summary>The declared block size is in range and not a power of two.</summary>
    BlockSizeNotPowerOfTwo,

    /// <summary>A block is sized differently from the family.</summary>
    BlockSizeMismatch,

    /// <summary>A block's base id is not a multiple of the block size.</summary>
    BlockUnaligned,
}

/// <summary>
/// The first rule a family breaks, typed. <see cref="ContentFamily"/> throws it as the plain
/// <see cref="ArgumentException"/> or <see cref="ArgumentOutOfRangeException"/> its callers catch, and the bundle
/// reader maps <see cref="Rule"/> to a <see cref="ContentAuthoringRefusal"/> without parsing the message.
/// </summary>
internal sealed class ContentFamilyFinding
{
    internal ContentFamilyFinding(ContentFamilyRule rule, int blockIndex, string parameterName, string message)
    {
        Rule = rule;
        BlockIndex = blockIndex;
        ParameterName = parameterName;
        Message = message;
    }

    /// <summary>The rule the family breaks.</summary>
    public ContentFamilyRule Rule { get; }

    /// <summary>The index of the block at fault in the block list, or -1 for a rule about the family itself.</summary>
    public int BlockIndex { get; }

    /// <summary>The argument the exception names.</summary>
    public string ParameterName { get; }

    /// <summary>The explanation, without the parameter suffix the BCL exception appends.</summary>
    public string Message { get; }

    /// <summary>The refused block size, set for the two block size rules.</summary>
    public int? BlockSize { get; init; }

    /// <summary>The exception <see cref="ContentFamily"/> throws for this finding.</summary>
    public ArgumentException ToException()
        => Rule is ContentFamilyRule.BlockSizeOutsideRange or ContentFamilyRule.BlockSizeNotPowerOfTwo
            ? new ArgumentOutOfRangeException(ParameterName, BlockSize, Message)
            : new ArgumentException(Message, ParameterName);
}
