using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Client.Internal;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>
/// Drives <see cref="BugReportFlow"/> frame by frame with a scripted capture and sink, a real submitter and offline
/// queue in a temporary directory, and a headless <see cref="BugReportScreen"/>. The test thread is the frame thread
/// for the length of each <see cref="Step"/>. Every background task a flow starts is awaited before the directory goes.
/// </summary>
public abstract class BugReportFlowHarness : IAsyncLifetime
{
    private protected const float Dt = 1f / 60f;

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "ke-bugreport-flow-" + Guid.NewGuid().ToString("N"));
    private protected readonly ManualTimeProvider _time = new();
    private protected readonly FakeCapture _capture = new();
    private protected readonly FakeSink _sink = new();
    private protected readonly BugReportSectionSet _sections = new();
    private protected readonly DesignViewport _viewport = new(1280, 720);
    private protected readonly List<string> _captured = new();
    private protected readonly List<BugReportSubmitResult> _submitted = new();
    private protected readonly List<bool> _submittedOnFrameThread = new();
    private protected readonly List<(int Width, int Height, bool OnFrameThread)> _textures = new();
    private protected readonly List<string> _saved = new();
    private protected readonly List<Task> _work = new();
    private protected readonly InMemorySink _logs = new();
    private protected readonly ProbeSection _probe;
    private protected string? _subject;
    private protected Exception? _screenFailure;
    int _frameThread;
    bool _inUpdate;

    private protected BugReportFlowHarness()
    {
        _probe = new ProbeSection((writer, context) =>
        {
            writer.WriteStartObject();
            writer.WriteString("subject", context.Subject);
            writer.WriteEndObject();
        });
        _sections.Add(_probe);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Waits for every background task the flows started, so none outlives the test or its directory.</summary>
    public async Task DisposeAsync()
    {
        await Task.WhenAll(_work).WaitAsync(TimeSpan.FromSeconds(30));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        else if (File.Exists(_root)) File.Delete(_root);
    }

    private protected BugReportFlow NewFlow(BugReportServiceOptions? options = null, string? queueDirectory = null)
    {
        options ??= new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            SaveTriggerBindings = _saved.Add,
        };
        var queue = new BugReportOfflineQueue(queueDirectory ?? Path.Combine(_root, "pending"), _time);
        var flow = new BugReportFlow(options, _capture, _sections, new BugReportSubmitter(_sink, queue), new ReportRedactor(),
            () => _subject,
            model => _screenFailure is { } failure
                ? throw failure
                : new BugReportScreen(model, new BugReportScreenResources(new FixedMeasurer(), null, null, _viewport)),
            image =>
            {
                _textures.Add((image.Width, image.Height, OnFrameThread()));
                return null;
            },
            _time);
        flow.ReportCaptured += _captured.Add;
        flow.ReportSubmitted += result =>
        {
            _submitted.Add(result);
            _submittedOnFrameThread.Add(OnFrameThread());
        };
        return flow;
    }

    private protected static InputState Frame(Key[]? pressed = null, Key[]? held = null, string? text = null)
    {
        var down = new HashSet<Key>(held ?? []);
        var edges = new HashSet<Key>(pressed ?? []);
        down.UnionWith(edges);
        return new InputState(down, edges, new HashSet<Key>(), new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 1280, 720, textInput: text ?? "", textInputAvailable: text is not null);
    }

    /// <summary>Runs one frame. The calling thread is the frame thread for the length of the call.</summary>
    private protected bool Step(BugReportFlow flow, InputState? input = null)
    {
        _frameThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _inUpdate, true);
        try
        {
            return flow.Update(input ?? Frame(), Dt, _viewport);
        }
        finally
        {
            Volatile.Write(ref _inUpdate, false);
            if (flow.PendingWork is { } work && !_work.Contains(work)) _work.Add(work);
        }
    }

    /// <summary>Calls <paramref name="call"/> with the calling thread marked as the frame thread.</summary>
    private protected T OnFrame<T>(Func<T> call)
    {
        _frameThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _inUpdate, true);
        try
        {
            return call();
        }
        finally
        {
            Volatile.Write(ref _inUpdate, false);
        }
    }

    /// <summary>Signs in as discord:42, creates the flow and settles its sign-in flush before the test goes on.</summary>
    private protected async Task<BugReportFlow> SignedIn(BugReportServiceOptions? options = null, string? queueDirectory = null)
    {
        _subject = "discord:42";
        BugReportFlow flow = NewFlow(options, queueDirectory);
        Assert.False(Step(flow));
        await Settle(flow);
        return flow;
    }

    /// <summary>Routes this async flow's log lines into <see cref="_logs"/> until disposed.</summary>
    private protected IDisposable CaptureLogs()
    {
        var options = new LoggerOptions { Synchronous = true };
        options.Sinks.Add(_logs);
        return Log.Use(new LogManager(options));
    }

    private protected IEnumerable<LogEntry> Warnings => _logs.Entries.Where(e => e.Level == LogLevel.Warn);

    /// <summary>Waits until <paramref name="count"/> timers wait on the manual clock.</summary>
    private protected async Task WaitForTimers(int count)
    {
        for (int i = 0; _time.ActiveTimers < count; i++)
        {
            Assert.True(i < 3000, "The flow never armed its screenshot timers.");
            await Task.Delay(10);
        }
    }

    /// <summary>True when called from inside <see cref="Step"/> on its thread.</summary>
    private protected bool OnFrameThread() => Volatile.Read(ref _inUpdate) && Environment.CurrentManagedThreadId == _frameThread;

    /// <summary>Presses F10, then runs the frame that pushes the screen, and returns the screen.</summary>
    private protected BugReportScreen Open(BugReportFlow flow)
    {
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));
        Assert.True(Step(flow, Frame(held: [Key.F10])));
        return Assert.IsType<BugReportScreen>(Assert.Single(flow.Screens.Screens));
    }

    /// <summary>Types a description and sends with Ctrl+Enter. Returns what the send frame returned.</summary>
    private protected bool Send(BugReportFlow flow, string description = "Fell through")
    {
        Assert.True(Step(flow, Frame(text: description)));
        return Step(flow, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
    }

    /// <summary>Awaits the latest background task, then runs one frame so its posted work lands.</summary>
    private protected async Task Settle(BugReportFlow flow)
    {
        if (flow.PendingWork is { } work) await work.WaitAsync(TimeSpan.FromSeconds(30));
        Step(flow);
    }

    private protected string Toast(BugReportFlow flow) => flow.Toasts.Active[0].Message.Resolve();

    private protected static string[] EntryNames(BugReportSubmission submission)
    {
        using var zip = new ZipArchive(new MemoryStream(submission.Bundle.ToArray()), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    private protected static string EntryText(BugReportSubmission submission, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(submission.Bundle.ToArray()), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private protected sealed class ProbeSection(Action<Utf8JsonWriter, BugReportContext> write, string key = "probe") : IBugReportSection
    {
        public string Key => key;

        public int MaxBytes => 4096;

        public int Writes { get; private set; }

        public List<string?> Subjects { get; } = new();

        public void Write(Utf8JsonWriter writer, BugReportContext context)
        {
            Writes++;
            Subjects.Add(context.Subject);
            write(writer, context);
        }
    }

    private sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 20f;

        public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);

        public Vector2 Measure(ReadOnlySpan<char> text) => new(text.Length * 10f, LineHeight);
    }
}
