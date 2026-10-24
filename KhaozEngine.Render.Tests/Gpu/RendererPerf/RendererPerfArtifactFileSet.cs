using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Keeps handles alive so file IDs cannot be recycled while evidence is decoded. Unix opens and
// entry queries are relative to retained no-follow directory handles. Windows checks retained
// parent and leaf IDs with reparse-point handles. Separate compatible-writer exclusion lasts
// from before the first content read through final validation and disposal.
internal sealed class RendererPerfArtifactFileSet : IDisposable
{
    sealed record Lease(Lease? Parent, string Path, SafeFileHandle Handle, RendererPerfFileStatus Status);
    readonly IRendererPerfArtifactIdentity _identity;
    readonly RendererPerfUnitBudget? _budget;
    readonly Dictionary<string, Lease> _directories = new(StringComparer.Ordinal);
    readonly List<Lease> _leases = [];
    readonly HashSet<RendererPerfFileId> _fileIds = [];
    readonly string _root;
    internal RendererPerfArtifactFileSet(string directory, RendererPerfUnitBudget? budget, IRendererPerfArtifactIdentity identity)
    {
        _identity = identity; _budget = budget;
        _root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        try { _directories.Add(_root, Acquire(null, _root, true)); }
        catch { Dispose(); throw; }
    }
    Lease Acquire(Lease? parent, string path, bool directory)
    {
        var handle = _identity.Open(parent?.Handle, path, directory);
        try
        {
            var status = _identity.Handle(handle);
            if (status.Directory != directory || status != _identity.Entry(parent?.Handle, path))
                throw new IOException("Artifact opened handle differs from its no-follow directory entry.");
            if (!directory && !_fileIds.Add(status.Id)) throw new IOException("Duplicate file identity or hard-link artifact alias.");
            if (!directory) _identity.ExcludeWriters(handle);
            var lease = new Lease(parent, path, handle, status);
            _leases.Add(lease);
            return lease;
        }
        catch { handle.Dispose(); throw; }
    }
    internal FileStream Open(string relative)
    {
        RendererPerfSidecarRegistry.Path(relative);
        var parts = relative.Split('/');
        var parent = _directories[_root];
        for (int n = 0; n < parts.Length - 1; n++)
        {
            string path = System.IO.Path.Combine(parent.Path, parts[n]);
            if (!_directories.TryGetValue(path, out var found))
                _directories.Add(path, found = Acquire(parent, path, true));
            Validate(found); parent = found;
        }
        var file = Acquire(parent, System.IO.Path.Combine(parent.Path, parts[^1]), false);
        return new FileStream(file.Handle, FileAccess.Read, 65536, false);
    }
    void Validate(Lease lease)
    {
        if (_identity.Handle(lease.Handle) != lease.Status || _identity.Entry(lease.Parent?.Handle, lease.Path) != lease.Status)
            throw new IOException("Artifact handle, directory entry or containment changed during verification.");
    }
    internal void ValidateAll()
    {
        foreach (var lease in _leases)
        {
            _budget?.Check("renderer.verify.identity.before");
            Validate(lease);
            _budget?.Check("renderer.verify.identity.after");
        }
        // Recheck the directory chain after leaf queries. A replaced parent cannot make an earlier
        // file's retained handle stand in for a newly named subtree at certification.
        foreach (var directory in _directories.Values) Validate(directory);
    }
    public void Dispose() { foreach (var lease in _leases) lease.Handle.Dispose(); }
}
