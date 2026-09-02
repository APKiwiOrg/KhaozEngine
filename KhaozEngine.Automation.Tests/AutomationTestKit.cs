using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Automation;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests;

/// <summary>Set an environment variable for the life of the scope and put back exactly what was there before,
/// including "not set at all" (which is distinct from empty).</summary>
sealed class EnvironmentScope : IDisposable
{
    readonly string _name;
    readonly string? _previous;

    public EnvironmentScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}

/// <summary>A temp directory that deletes itself, standing in for the app data directory the handshake file goes into.</summary>
sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ke-automation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Shared builders: real input snapshots and request payloads, so a test reads as the case it is making.</summary>
static class AutomationTestKit
{
    /// <summary>A real frame snapshot with whatever the case needs held, at <paramref name="position"/>.</summary>
    public static InputState Real(
        Vector2 position = default,
        bool windowFocused = true,
        IEnumerable<Key>? keysDown = null,
        IEnumerable<MouseButton>? mouseDown = null,
        int width = 1280,
        int height = 720)
    {
        var down = new HashSet<Key>(keysDown ?? Array.Empty<Key>());
        var buttons = new HashSet<MouseButton>(mouseDown ?? Array.Empty<MouseButton>());
        return new InputState(
            down, new HashSet<Key>(), new HashSet<Key>(),
            buttons, new HashSet<MouseButton>(),
            position, Vector2.Zero, 0f, width, height,
            windowFocused: windowFocused);
    }

    /// <summary>Parse a wire line into a request, failing the test if it will not parse.</summary>
    public static AutomationRequest Parse(string line)
    {
        if (!AutomationRequest.TryParse(line, out AutomationRequest? request, out string? error))
            throw new InvalidOperationException("expected a parseable request line, got: " + error);
        return request!;
    }

    /// <summary>Parse a JSON line into a detached element, for asserting on a reply's shape.</summary>
    public static JsonElement Json(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
}
