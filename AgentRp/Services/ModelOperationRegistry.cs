using System.Collections.Concurrent;

namespace AgentRp.Services;

public sealed class ModelOperationRegistry : IModelOperationRegistry
{
    private readonly ConcurrentDictionary<Guid, RegisteredOperation> _operations = new();

    public IModelOperationHandle Start(Guid? operationId = null)
    {
        var id = operationId ?? Guid.NewGuid();
        var operation = new RegisteredOperation(id);

        if (!_operations.TryAdd(id, operation))
        {
            operation.Dispose();
            throw new InvalidOperationException($"Starting model operation {id} failed because that operation id is already active.");
        }

        return new ModelOperationHandle(this, operation);
    }

    public ModelOperationView? GetOperation(Guid operationId) =>
        _operations.TryGetValue(operationId, out var operation)
            ? new ModelOperationView(operationId, operation.CancellationTokenSource.IsCancellationRequested)
            : null;

    public bool IsActive(Guid operationId) => _operations.ContainsKey(operationId);

    public bool TryCancel(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
            return false;

        operation.CancellationTokenSource.Cancel();
        return true;
    }

    private void Complete(RegisteredOperation operation)
    {
        if (_operations.TryRemove(new KeyValuePair<Guid, RegisteredOperation>(operation.OperationId, operation)))
            operation.Dispose();
    }

    private sealed class RegisteredOperation(Guid operationId) : IDisposable
    {
        public Guid OperationId { get; } = operationId;

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public void Dispose() => CancellationTokenSource.Dispose();
    }

    private sealed class ModelOperationHandle(ModelOperationRegistry registry, RegisteredOperation operation) : IModelOperationHandle
    {
        private int _disposed;

        public Guid OperationId => operation.OperationId;

        public CancellationToken CancellationToken => operation.CancellationTokenSource.Token;

        public bool IsCancellationRequested => operation.CancellationTokenSource.IsCancellationRequested;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            registry.Complete(operation);
        }
    }
}
