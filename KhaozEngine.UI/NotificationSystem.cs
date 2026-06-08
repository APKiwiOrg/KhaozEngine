using System;
using System.Collections.Generic;

namespace KhaozEngine.UI;

/// <summary>
/// Type of notification, controlling visual style (background tint and border color).
/// </summary>
public enum NotificationType
{
    /// <summary>Theme-tinted background with bright border.</summary>
    Standard,

    /// <summary>Yellow-tinted background with bright yellow border.</summary>
    Warning,

    /// <summary>Red-tinted background with bright red border.</summary>
    Danger
}

/// <summary>
/// A single notification entry managed by <see cref="NotificationSystem"/>.
/// </summary>
public sealed class Notification
{
    /// <summary>The message displayed in the notification.</summary>
    public string Message { get; init; } = "";

    /// <summary>Visual type controlling colors.</summary>
    public NotificationType Type { get; init; }

    /// <summary>Total duration in seconds. Zero or negative = infinite (no auto-dismiss).</summary>
    public double Duration { get; init; }

    /// <summary>Time remaining before auto-dismiss. Counts down each frame.</summary>
    public double Remaining { get; set; }

    /// <summary>True if this notification never auto-dismisses.</summary>
    public bool IsInfinite => Duration <= 0;
}

/// <summary>
/// Manages a stack of toast notifications displayed at the top-right of the screen.
/// Notifications auto-dismiss after a configurable duration (default 6 seconds,
/// real time  -- not affected by simulation speed). Optionally infinite.
/// Tapping/clicking a notification dismisses it immediately.
/// </summary>
public sealed class NotificationSystem
{
    private readonly List<Notification> _active = [];

    /// <summary>Currently visible notifications (newest first).</summary>
    public IReadOnlyList<Notification> Active => _active;

    /// <summary>Maximum notifications displayed simultaneously.</summary>
    public int MaxVisible { get; set; } = 5;

    /// <summary>
    /// Shows a new notification.
    /// </summary>
    /// <param name="message">Text to display.</param>
    /// <param name="type">Visual style.</param>
    /// <param name="duration">
    /// Duration in seconds before auto-dismiss. Pass 0 or negative for infinite.
    /// </param>
    public void Show(string message, NotificationType type, double duration = 6.0)
    {
        var notification = new Notification
        {
            Message = message,
            Type = type,
            Duration = duration,
            Remaining = duration
        };

        // Insert at the beginning (newest first)
        _active.Insert(0, notification);

        // Trim excess
        while (_active.Count > MaxVisible)
            _active.RemoveAt(_active.Count - 1);
    }

    /// <summary>
    /// Dismisses the notification at the given index.
    /// </summary>
    public void Dismiss(int index)
    {
        if (index >= 0 && index < _active.Count)
            _active.RemoveAt(index);
    }

    /// <summary>
    /// Ticks all notification timers and removes expired ones.
    /// Call once per frame with real delta seconds (not sim delta).
    /// </summary>
    public void Update(double realDelta)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            Notification n = _active[i];
            if (n.IsInfinite) continue;

            n.Remaining -= realDelta;
            if (n.Remaining <= 0)
                _active.RemoveAt(i);
        }
    }
}
