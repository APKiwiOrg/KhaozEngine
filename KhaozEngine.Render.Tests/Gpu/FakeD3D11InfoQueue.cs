using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A device-free <see cref="ID3D11InfoQueueSource"/>: a list of messages, a clear counter, and an optional
    /// throw. Nothing here is Direct3D, which is the point. The real Windows reader lands with the device row,
    /// because <c>ID3D11InfoQueue::GetMessageW</c> is a two-pass call into a caller-allocated buffer and Vortice
    /// 2.3.0 exposes only that raw form, so it is interop a Windows machine has to exercise before anyone should
    /// believe it.
    /// </summary>
    internal sealed class FakeD3D11InfoQueue : ID3D11InfoQueueSource
    {
        readonly List<D3D11InfoMessage> _messages = new();

        /// <summary>Set to make every read throw, which is the pump's give-up path.</summary>
        internal bool ThrowOnRead { get; set; }

        /// <summary>How many times the pump emptied the queue. A pump that stopped clearing would let the queue
        /// grow without bound on exactly the session the rate limit exists for.</summary>
        internal int ClearCount { get; private set; }

        /// <summary>True once disposed, so the pump's ownership of the source is assertable.</summary>
        internal bool IsDisposed { get; private set; }

        internal void Add(D3D11InfoSeverity severity, int id, string text)
            => _messages.Add(new D3D11InfoMessage(severity, category: 3, id, text));

        internal void AddRepeated(D3D11InfoSeverity severity, int id, string text, int count)
        {
            for (int i = 0; i < count; i++) Add(severity, id, text);
        }

        public ulong StoredMessageCount => ThrowOnRead ? throw new InvalidOperationException("the fake is faulted")
            : (ulong)_messages.Count;

        public D3D11InfoMessage Read(ulong index)
            => ThrowOnRead ? throw new InvalidOperationException("the fake is faulted") : _messages[(int)index];

        public void ClearStoredMessages()
        {
            ClearCount++;
            _messages.Clear();
        }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>An <see cref="ILogger"/> that keeps what it was told, at which level, so a test can assert both.
    /// The pump's whole observable behaviour is what it logs and at what level, so this is the only way to see
    /// it.</summary>
    internal sealed class RecordingLogger : ILogger
    {
        internal List<string> Infos { get; } = new();
        internal List<string> Warns { get; } = new();
        internal List<string> Errors { get; } = new();

        public string Category => "test";

        public bool IsEnabled(LogLevel level) => true;

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            switch (level)
            {
                case LogLevel.Info: Infos.Add(message); break;
                case LogLevel.Warn: Warns.Add(message); break;
                case LogLevel.Error: case LogLevel.Fatal: Errors.Add(message); break;
                default: break;
            }
        }

        public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
        public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
        public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);
        public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);
        public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
        public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
    }
}
