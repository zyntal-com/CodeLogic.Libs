using CL.Storage.Abstractions;

namespace CL.Storage.Registry;

internal sealed class BackendEntry
{
    private readonly Lock _gate = new();
    private readonly bool _ownsBackend;
    private TaskCompletionSource? _drained;
    private int _activeOperations;
    private bool _retired;
    private int _disposed;

    public BackendEntry(IStorageBackend backend, bool ownsBackend)
    {
        Backend = backend;
        _ownsBackend = ownsBackend;
    }

    public IStorageBackend Backend { get; }

    public bool TryAcquire(out BackendOperationLease? lease)
    {
        lock (_gate)
        {
            if (_retired)
            {
                lease = null;
                return false;
            }

            checked { _activeOperations++; }
            lease = new BackendOperationLease(this);
            return true;
        }
    }

    public async Task RetireAndDisposeAsync()
    {
        Task waitForDrain;
        lock (_gate)
        {
            _retired = true;
            if (_activeOperations == 0)
            {
                waitForDrain = Task.CompletedTask;
            }
            else
            {
                _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waitForDrain = _drained.Task;
            }
        }

        await waitForDrain.ConfigureAwait(false);
        if (_ownsBackend && Interlocked.Exchange(ref _disposed, 1) == 0)
            await Backend.DisposeAsync().ConfigureAwait(false);
    }

    private void Release()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _activeOperations--;
            if (_retired && _activeOperations == 0)
                drained = _drained;
        }
        drained?.TrySetResult();
    }

    internal sealed class BackendOperationLease : IDisposable
    {
        private BackendEntry? _entry;

        public BackendOperationLease(BackendEntry entry) => _entry = entry;
        public IStorageBackend Backend => _entry?.Backend ?? throw new ObjectDisposedException(nameof(BackendOperationLease));

        public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();
    }
}
