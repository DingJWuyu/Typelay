using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// One source lifetime, not one grant. The existing LL thread is the sole writer;
// the source GUI thread is the sole consumer. Neither side searches or resets
// this FIFO when a newer candidate is installed.
internal sealed class SourceInputWitness : IDisposable
{
    internal const int Capacity = 256, HeaderBytes = 136, RecordBytes = 64;
    internal const int Bytes = HeaderBytes + Capacity * RecordBytes;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private long _sequence;
    private int _disposed;
    internal WindowsTarget Target
    {
        get;
    }
    internal uint ThreadId
    {
        get;
    }
    internal ulong GrantNonce
    {
        get; set;
    }
    internal SafeMemoryMappedViewHandle Handle => _view.SafeMemoryMappedViewHandle;
    internal bool Ready
    {
        get
        {
            uint poison = _view.ReadUInt32(8), ready = _view.ReadUInt32(12);
            Thread.MemoryBarrier();
            return _view.ReadUInt32(0) == 2 && _view.ReadUInt32(4) == Bytes && poison == 0 && ready == 1;
        }
    }

    internal SourceInputWitness(WindowsTarget target)
    {
        Target = target;
        ThreadId = Native.GetWindowThreadProcessId((nint)target.ControlHwnd, out uint process);
        if (target.Lifetime == Guid.Empty || ThreadId == 0 || process != target.ProcessId)
            throw new InvalidOperationException("Source witness lifetime unavailable.");
        byte[] lifetime = target.Lifetime.ToByteArray();
        string name = "Local\\TypeLay.SourceWitness." + Convert.ToHexString(lifetime).ToLowerInvariant();
        _mapping = MemoryMappedFile.CreateNew(name, Bytes, MemoryMappedFileAccess.ReadWrite);
        try
        {
            _view = _mapping.CreateViewAccessor(0, Bytes, MemoryMappedFileAccess.ReadWrite);
            _view.Write(0, 2u);
            _view.Write(4, (uint)Bytes);
            _view.WriteArray(16, lifetime, 0, lifetime.Length);
            _view.Write(32, target.ControlHwnd);
            _view.Write(40, target.RootHwnd);
            _view.Write(48, process);
            _view.Write(52, ThreadId);
        }
        catch
        {
            _view?.Dispose();
            _mapping.Dispose();
            throw;
        }
    }

    // Mapping readiness only, on the actual LL thread before source-install.
    // Recording remains dormant until native publishes the exact attach boundary.
    internal void Start()
    {
        Thread.MemoryBarrier();
        _view.Write(12, 1u);
    }

    internal bool Publish(Native.KeyData input, bool repeated, bool eligible, long observedAtMs)
    {
        uint active = _view.ReadUInt32(132);
        Thread.MemoryBarrier();
        // Before the source reader is bound there must be no orphan records.
        // Refuse this candidate, but do not poison an observation not yet begun.
        if (active == 0)
            return false;
        ulong initial = _view.ReadUInt64(112);
        if (active != 1 || initial == 0)
        {
            Poison();
            return false;
        }
        // The source excludes this exact initial prefix before reading its head.
        // Same-tick legitimate input passes normally but cannot accept a draft.
        if (unchecked((int)(input.Time - (uint)initial)) <= 0)
            return false;
        long consumed = _view.ReadInt64(64);
        Thread.MemoryBarrier();
        long sequence = _sequence + 1;
        if (!Ready || consumed < 0 || consumed > _sequence || sequence <= 0
            || sequence - consumed > Capacity || _view.ReadInt64(56) != _sequence)
        {
            Poison();
            return false;
        }
        int offset = HeaderBytes + (int)((sequence - 1) % Capacity) * RecordBytes;
        _view.Write(offset, 0L);
        _view.Write(offset + 8, GrantNonce);
        _view.Write(offset + 16, observedAtMs);
        _view.Write(offset + 24, Target.ControlHwnd);
        _view.Write(offset + 32, input.Time);
        _view.Write(offset + 36, input.Key);
        _view.Write(offset + 40, input.Scan);
        _view.Write(offset + 44, input.Flags);
        _view.Write(offset + 48, eligible ? 1u : 0u);
        _view.Write(offset + 52, repeated ? 1u : 0u);
        _view.Write(offset + 56, 0UL);
        Thread.MemoryBarrier();
        _view.Write(offset, sequence);
        Thread.MemoryBarrier();
        _view.Write(56, sequence);
        _sequence = sequence;
        return true;
    }

    internal void Poison()
    {
        Thread.MemoryBarrier();
        _view.Write(8, 1u);
    }
    internal void Stop()
    {
        _view.Write(12, 0u);
        Poison();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            Stop();
        }
        finally
        {
            _view.Dispose();
            _mapping.Dispose();
        }
    }
}
