using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The rules behind <see cref="BugReportScreen"/>: what the player typed and picked, what media is available and
/// whether the report can be sent. The screen owns the widgets and copies their state in here every frame. Frame
/// thread only.
/// </summary>
internal sealed class BugReportScreenModel
{
    const string DefaultCategory = "Other";

    readonly string[] _categories;
    string _description = "";
    int _categoryIndex;
    bool _includeScreenshot = true;
    bool _includeClip = true;

    /// <summary>Creates the model for one report.</summary>
    /// <param name="categories">The category ids, in display order. At least one.</param>
    /// <param name="signedIn">Whether a player is signed in, so the report can go out now.</param>
    /// <param name="screenshotAvailable">Whether the trigger frame's screenshot can be attached.</param>
    /// <param name="clipAvailable">Whether a clip was recorded.</param>
    public BugReportScreenModel(IReadOnlyList<string> categories, bool signedIn, bool screenshotAvailable, bool clipAvailable)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count == 0) throw new ArgumentException("At least one category is required.", nameof(categories));
        _categories = new string[categories.Count];
        for (int i = 0; i < _categories.Length; i++)
        {
            _categories[i] = categories[i] ?? throw new ArgumentException("A category id is null.", nameof(categories));
            if (_categoryIndex == 0 && string.Equals(_categories[i], DefaultCategory, StringComparison.Ordinal))
                _categoryIndex = i;
        }
        SignedIn = signedIn;
        ScreenshotAvailable = screenshotAvailable;
        ClipAvailable = clipAvailable;
    }

    /// <summary>The category ids, in display order.</summary>
    public IReadOnlyList<string> Categories => _categories;

    /// <summary>
    /// The player's description, clamped to <see cref="BugReportLimits.MaxDescriptionChars"/> UTF-16 units without
    /// splitting a surrogate pair. Null reads back as empty.
    /// </summary>
    public string Description
    {
        get => _description;
        set => _description = Clamp(value ?? "");
    }

    /// <summary>The selected index into <see cref="Categories"/>. Defaults to <c>Other</c> when listed, else 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <see cref="Categories"/>.</exception>
    public int CategoryIndex
    {
        get => _categoryIndex;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, _categories.Length);
            _categoryIndex = value;
        }
    }

    /// <summary>The selected category id.</summary>
    public string Category => _categories[_categoryIndex];

    /// <summary>Whether the screenshot goes into the report. Always false while it is unavailable.</summary>
    public bool IncludeScreenshot
    {
        get => _includeScreenshot && ScreenshotAvailable;
        set => _includeScreenshot = value;
    }

    /// <summary>Whether the clip goes into the report. Always false while it is unavailable.</summary>
    public bool IncludeClip
    {
        get => _includeClip && ClipAvailable;
        set => _includeClip = value;
    }

    /// <summary>Whether a player is signed in.</summary>
    public bool SignedIn { get; }

    /// <summary>Whether the screenshot can be attached. The flow clears it when the capture faults.</summary>
    public bool ScreenshotAvailable { get; set; }

    /// <summary>Whether a clip was recorded.</summary>
    public bool ClipAvailable { get; }

    /// <summary>Whether the description has a non-whitespace character.</summary>
    public bool CanSend
    {
        get
        {
            foreach (char c in _description)
                if (!char.IsWhiteSpace(c)) return true;
            return false;
        }
    }

    /// <summary>Copies the description, category and media choices into <paramref name="draft"/>.</summary>
    public void ApplyTo(BugReportDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Description = Description;
        draft.Category = Category;
        draft.IncludeScreenshot = IncludeScreenshot;
        draft.IncludeClip = IncludeClip;
    }

    static string Clamp(string value)
    {
        int max = BugReportLimits.MaxDescriptionChars;
        if (value.Length <= max) return value;
        int end = char.IsLowSurrogate(value[max]) && char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..end];
    }
}
