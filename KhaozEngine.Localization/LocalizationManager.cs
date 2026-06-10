using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;

namespace KhaozEngine.Localization;

/// <summary>
/// Manages localization settings for a game: retrieving the cultures backed by satellite
/// resources, and setting the current thread culture.
/// </summary>
public class LocalizationManager
{
    /// <summary>
    /// The culture code the game defaults to.
    /// </summary>
    public const string DEFAULT_CULTURE_CODE = "en-US";
}
