using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TypeLay.Core;

namespace TypeLay.App;

// Inactive prototype endpoint, installed only by the isolated desktop tests.
// No RPC, capability publication, owned-practice identity or default route uses
// it. The fixture knows its two queues do not attach; ordinary apps do not give
// us that guarantee. A timeout parameter is not a hard real-time hook bound.
internal sealed class StockEditActivation : IDisposable
{
    internal sealed record Result(bool Claimed, WriteReceipt? Receipt, bool ExceededBudget, double ElapsedMs);
    private const int Bytes = 49336, Header = 72;
    internal const uint BudgetMs = 5;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly nint _library;
    private readonly ActivateNative _activate;
    private readonly ulong _nonce, _command;
    private readonly uint _message;
    private int _references = 1, _disposed, _invoked;
    internal AcceptancePermit Permit
    {
        get;
    }

    [SupportedOSPlatform("windows")]
    internal StockEditActivation(ulong nonce, ulong command, Guid generation, AcceptancePermit permit)
    {
        Permit = permit;
        _nonce = nonce;
        _command = command;
        _mapping = MemoryMappedFile.OpenExisting($"Local\\TypeLay.Source.{nonce:x16}", MemoryMappedFileRights.ReadWrite);
        try
        {
            _view = _mapping.CreateViewAccessor(0, Bytes, MemoryMappedFileAccess.ReadWrite);
            byte[] identity = new byte[16];
            _view.ReadArray(16, identity, 0, 16);
            if (_view.ReadUInt32(0) != 2 || _view.ReadUInt32(4) != Bytes || new Guid(identity) != generation)
                throw new InvalidOperationException("Source activation generation mismatch.");
            _view.ReadArray(32, identity, 0, 16);
            if (new Guid(identity) != permit.Plan.Id || command <= _view.ReadUInt64(56))
                throw new InvalidOperationException("Source activation grant mismatch.");
            _library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "TypeLay.NativeInput.dll"));
            _activate = Marshal.GetDelegateForFunctionPointer<ActivateNative>(
                NativeLibrary.GetExport(_library, "TypeLaySourceActivate"));
            _message = RegisterWindowMessageW("TypeLay.NativeInput.SourceActivate.v2");
            if (_message == 0)
                throw new InvalidOperationException("Source activation message unavailable.");
        }
        catch
        {
            if (_library != 0)
                NativeLibrary.Free(_library);
            _view?.Dispose();
            _mapping.Dispose();
            throw;
        }
    }

    internal Result Activate()
    {
        int references;
        do
        {
            references = Volatile.Read(ref _references);
            if (references == 0)
                return new(false, null, false, 0);
        } while (Interlocked.CompareExchange(ref _references, references + 1, references) != references);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _invoked, 1) != 0)
                return new(false, null, false, 0);
            long started = Stopwatch.GetTimestamp();
            uint flags = _activate(_view.SafeMemoryMappedViewHandle, Bytes, _nonce, _command, BudgetMs, _message);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            bool claimed = (flags & 1) != 0;
            WriteReceipt? receipt = null;
            if ((flags & 2) != 0)
            {
                uint status = _view.ReadUInt32(Header + 4);
                Denial reason = (Denial)_view.ReadUInt32(Header + 8);
                if (Enum.IsDefined(reason))
                    receipt = status switch
                    {
                        1 when claimed && reason == Denial.None => new(WriteStatus.Verified, reason),
                        2 when reason != Denial.None => new(WriteStatus.RejectedBeforeMutation, reason),
                        3 when claimed => new(WriteStatus.OutcomeUnknown, reason),
                        _ => null
                    };
            }
            return new(claimed, receipt, (flags & 4) != 0 || elapsed > BudgetMs, elapsed);
        }
        finally { Release(); }
    }

    public void Dispose()
    {
        // This closes only the adapter's view. The Broker remains responsible
        // for the grant mapping and hook through exact source retirement ACK.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Release();
    }
    private void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0)
            return;
        _view.Dispose();
        _mapping.Dispose();
        NativeLibrary.Free(_library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ActivateNative(SafeMemoryMappedViewHandle view, uint bytes, ulong nonce,
        ulong command, uint timeout, uint message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);
}
