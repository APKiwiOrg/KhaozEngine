using System;
using System.Collections.Generic;

namespace KhaozEngine.Social;

/// <summary>
/// A provider-neutral rich-presence descriptor. A game fills the fields it wants; empty/default
/// fields are omitted by the backend. Only <see cref="Details"/>, <see cref="State"/> and
/// <see cref="StartTimestampUtc"/> are required for a basic "playing X" presence.
/// </summary>
public readonly record struct RichPresence
{
    /// <summary>First line on the profile (e.g. "In the overworld").</summary>
    public string? Details { get; init; }

    /// <summary>Second line on the profile (e.g. "Solo - 04:12").</summary>
    public string? State { get; init; }

    /// <summary>When set, the platform renders an elapsed timer counting up from this instant.</summary>
    public DateTime? StartTimestampUtc { get; init; }

    /// <summary>When set, the platform renders a countdown to this instant.</summary>
    public DateTime? EndTimestampUtc { get; init; }

    /// <summary>Large profile image (asset key + hover text).</summary>
    public PresenceImage LargeImage { get; init; }

    /// <summary>Small profile image (asset key + hover text).</summary>
    public PresenceImage SmallImage { get; init; }

    /// <summary>Party grouping (id + current/max size). A non-zero <see cref="PresenceParty.Max"/> shows "(size of max)".</summary>
    public PresenceParty Party { get; init; }

    /// <summary>Opaque secret enabling a "Join Game" action on the profile; the game's netcode encodes/decodes it.</summary>
    public string? JoinSecret { get; init; }

    /// <summary>Opaque secret enabling a "Spectate" action.</summary>
    public string? SpectateSecret { get; init; }

    /// <summary>Up to two profile buttons (label + URL). Ignored beyond the platform's limit.</summary>
    public IReadOnlyList<PresenceButton>? Buttons { get; init; }

    // The compiler-generated record-struct equality compares Buttons by reference, so a game that
    // rebuilds its button list each frame would defeat SocialPresenceController's content dedupe.
    // Compare Buttons structurally so equal-content presence is treated as equal regardless of list identity.
    public bool Equals(RichPresence other) =>
        Details == other.Details
        && State == other.State
        && Nullable.Equals(StartTimestampUtc, other.StartTimestampUtc)
        && Nullable.Equals(EndTimestampUtc, other.EndTimestampUtc)
        && LargeImage.Equals(other.LargeImage)
        && SmallImage.Equals(other.SmallImage)
        && Party.Equals(other.Party)
        && JoinSecret == other.JoinSecret
        && SpectateSecret == other.SpectateSecret
        && ButtonsEqual(Buttons, other.Buttons);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Details);
        hash.Add(State);
        hash.Add(StartTimestampUtc);
        hash.Add(EndTimestampUtc);
        hash.Add(LargeImage);
        hash.Add(SmallImage);
        hash.Add(Party);
        hash.Add(JoinSecret);
        hash.Add(SpectateSecret);
        if (Buttons is not null)
        {
            foreach (PresenceButton button in Buttons)
            {
                hash.Add(button);
            }
        }

        return hash.ToHashCode();
    }

    private static bool ButtonsEqual(IReadOnlyList<PresenceButton>? a, IReadOnlyList<PresenceButton>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A presence image: an uploaded asset key plus optional hover text. Default is "no image".</summary>
public readonly record struct PresenceImage(string? Key, string? Text);

/// <summary>Party grouping for presence. <see cref="Id"/> groups members; <see cref="Size"/>/<see cref="Max"/> render "(n of m)".</summary>
public readonly record struct PresenceParty(string? Id, int Size, int Max);

/// <summary>A profile button: display label + URL to open.</summary>
public readonly record struct PresenceButton(string Label, string Url);
