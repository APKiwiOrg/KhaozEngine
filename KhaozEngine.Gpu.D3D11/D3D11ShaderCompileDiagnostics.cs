using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>One FXC attempt or DXBC cache hit. HLSL metadata is unavailable for cache hits, which do not
/// recreate HLSL or invoke FXC. Milliseconds excludes cross-compilation, fingerprinting and cache I/O.</summary>
internal readonly record struct D3D11ShaderCompileRecord(string SourceKey, string Program, GpuShaderStages Stage,
    int? HlslByteLength, string? HlslSha256, bool CacheHit, double Milliseconds,
    long? StartedTimestamp = null, long? EndedTimestamp = null);

/// <summary>FXC work summed across workers versus the monotonic span from first start to last completion.</summary>
internal readonly record struct D3D11ShaderCompileSummary(int PeakConcurrency, double SummedMilliseconds,
    double ElapsedMilliseconds);

/// <summary>Opt-in shader compile records for the current execution flow and its workers. Without an active
/// scope, diagnostic calls return before reading the clock, encoding or hashing, and allocate nothing.</summary>
internal sealed class D3D11ShaderCompileDiagnostics
{
    static AsyncLocal<Scope?>? current;
    readonly object gate = new();
    readonly List<D3D11ShaderCompileRecord> records = [];
    int active, peak;
    long firstStarted, lastEnded;

    /// <summary>Routes records from this execution flow to this collector. Dispose scopes in reverse creation
    /// order. Activate at the compile caller when a transport does not carry the caller's execution context.</summary>
    public IDisposable Use()
    {
        var slot = Volatile.Read(ref current);
        if (slot is null)
        {
            var created = new AsyncLocal<Scope?>();
            slot = Interlocked.CompareExchange(ref current, created, null) ?? created;
        }
        var scope = new Scope(slot, slot.Value, this);
        slot.Value = scope;
        return scope;
    }

    /// <summary>Returns a detached point-in-time copy in completion order, including failed FXC attempts.
    /// Reading while another worker compiles is safe.</summary>
    public D3D11ShaderCompileRecord[] GetRecords()
    {
        lock (gate) return records.ToArray();
    }

    internal D3D11ShaderCompileSummary GetSummary()
    {
        lock (gate)
        {
            double summed = 0;
            foreach (var record in records) summed += record.Milliseconds;
            double elapsed = peak == 0 ? 0 : Stopwatch.GetElapsedTime(firstStarted,
                active == 0 ? lastEnded : Stopwatch.GetTimestamp()).TotalMilliseconds;
            return new(peak, summed, elapsed);
        }
    }

    long Start()
    {
        lock (gate)
        {
            long timestamp = Stopwatch.GetTimestamp();
            if (peak == 0) firstStarted = timestamp;
            peak = Math.Max(peak, ++active);
            return timestamp;
        }
    }

    void Finish(D3D11ShaderCompileRecord record, long started)
    {
        lock (gate)
        {
            lastEnded = Stopwatch.GetTimestamp();
            active--;
            records.Add(record with
            {
                StartedTimestamp = started, EndedTimestamp = lastEnded,
                Milliseconds = Stopwatch.GetElapsedTime(started, lastEnded).TotalMilliseconds
            });
        }
    }

    internal static Measurement MeasureCompile(string key, string program, D3D11ShaderStage stage, string hlsl)
    {
        var scope = Volatile.Read(ref current)?.Value;
        return scope is null ? default : new Measurement(scope.Owner, key, program, stage, hlsl);
    }

    internal static void RecordCacheHit(string key, string program, D3D11ShaderStage stage)
    {
        var scope = Volatile.Read(ref current)?.Value;
        scope?.Owner.Add(new(key, program, ToGpuStage(stage), null, null, true, 0));
    }

    static GpuShaderStages ToGpuStage(D3D11ShaderStage stage) => stage switch
    {
        D3D11ShaderStage.Vertex => GpuShaderStages.Vertex,
        D3D11ShaderStage.Fragment => GpuShaderStages.Fragment,
        D3D11ShaderStage.Compute => GpuShaderStages.Compute,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    void Add(D3D11ShaderCompileRecord record)
    {
        lock (gate) records.Add(record);
    }

    internal readonly struct Measurement : IDisposable
    {
        readonly D3D11ShaderCompileDiagnostics? owner;
        readonly D3D11ShaderCompileRecord record;
        readonly long started;

        internal Measurement(D3D11ShaderCompileDiagnostics owner, string key, string program,
            D3D11ShaderStage stage, string hlsl)
        {
            this.owner = owner;
            byte[] bytes = Encoding.UTF8.GetBytes(hlsl);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            record = new(key, program, ToGpuStage(stage), bytes.Length, hash, false, 0);
            started = owner.Start();
        }

        public void Dispose()
        {
            if (owner is not null)
                owner.Finish(record, started);
        }
    }

    sealed class Scope(AsyncLocal<Scope?> slot, Scope? parent, D3D11ShaderCompileDiagnostics owner) : IDisposable
    {
        internal readonly Scope? Parent = parent;
        internal readonly D3D11ShaderCompileDiagnostics Owner = owner;

        public void Dispose()
        {
            var head = slot.Value;
            if (ReferenceEquals(head, this))
            {
                slot.Value = Parent;
                return;
            }
            for (var node = head; node is not null; node = node.Parent)
                if (ReferenceEquals(node, this))
                    throw new InvalidOperationException("Dispose shader diagnostic scopes in reverse creation order.");
            // An inherited execution flow can already have restored its own parent independently.
        }
    }
}
