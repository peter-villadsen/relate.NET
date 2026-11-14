using Relate.Net.Interop;
using static Relate.Net.Interop.NativeMethods;

namespace Relate.Net;

/// <summary>
/// A session on a <see cref="RelateDatabase"/> that runs Cypher statements, optionally inside an
/// explicit <see cref="RelateTransaction"/>.
/// </summary>
/// <remarks>
/// A connection is safe to share between threads: calls are serialized (one statement at a time).
/// For parallelism open one connection per worker. Disposing the connection rolls back an open
/// transaction.
/// </remarks>
public sealed class RelateConnection : IDisposable, IAsyncDisposable
{
    private readonly ConnectionHandle _handle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _namespace;
    private RelateTransaction? _transaction;
    private volatile bool _disposed;

    internal RelateConnection(RelateDatabase database, ConnectionHandle handle, string @namespace)
    {
        Database = database;
        _handle = handle;
        _namespace = @namespace;
    }

    /// <summary>The database this connection belongs to.</summary>
    public RelateDatabase Database { get; }

    /// <summary>The session namespace used by statements without a <c>USE</c> clause.</summary>
    public string Namespace => _namespace;

    /// <summary>The open explicit transaction, or <see langword="null"/>.</summary>
    public RelateTransaction? CurrentTransaction => _transaction;

    /// <summary><see langword="true"/> once the connection has been disposed.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Runs a Cypher statement and returns its fully materialized result.
    /// </summary>
    /// <param name="cypher">The Cypher statement.</param>
    /// <param name="parameters">
    /// Named parameters (<c>$name</c>): any <c>IDictionary</c> / <c>IReadOnlyDictionary</c> with
    /// string keys, an anonymous object, or any object whose public properties become parameters.
    /// </param>
    /// <exception cref="RelateException">The statement failed (syntax, runtime or engine error).</exception>
    /// <exception cref="ArgumentException">A parameter value cannot be sent to relate.</exception>
    /// <exception cref="ObjectDisposedException">The connection has been disposed.</exception>
    public QueryResult Query(string cypher, object? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(cypher);
        return Exclusive(() => QueryCore(cypher, parameters));
    }

    /// <summary>
    /// Runs a Cypher statement on a thread-pool thread. Cancellation is honoured until the statement
    /// starts; a running statement completes.
    /// </summary>
    /// <inheritdoc cref="Query" />
    public Task<QueryResult> QueryAsync(string cypher, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cypher);
        return ExclusiveAsync(() => QueryCore(cypher, parameters), cancellationToken);
    }

    /// <summary>
    /// Changes the session namespace (<c>neo4j</c>, <c>system</c> or <c>system_ontologies</c>).
    /// Not allowed while a transaction is open.
    /// </summary>
    /// <exception cref="RelateException">The namespace does not exist or a transaction is open.</exception>
    public unsafe void ChangeNamespace(string @namespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(@namespace);
        Exclusive(() =>
        {
            nint err = 0;
            Native.Check(relate_conn_set_namespace(_handle, @namespace, &err), err, "set namespace");
            _namespace = @namespace.ToLowerInvariant();
            return true;
        });
    }

    /// <summary>
    /// Begins an explicit transaction. Statements run on this connection (or through the returned
    /// transaction) belong to it until it is committed, rolled back or disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">A transaction is already open on this connection.</exception>
    public RelateTransaction BeginTransaction() => Exclusive(BeginCore);

    /// <inheritdoc cref="BeginTransaction" />
    public Task<RelateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        ExclusiveAsync(BeginCore, cancellationToken);

    /// <summary>Closes the connection, rolling back any open transaction.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _transaction?.MarkCompleted();
            _transaction = null;
            _handle.Dispose();
        }
        finally
        {
            _gate.Release();
        }

        Database.Forget(this);
    }

    /// <inheritdoc cref="Dispose" />
    public async ValueTask DisposeAsync()
    {
        await Task.Run(Dispose).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override string ToString() => $"RelateConnection({Database.Location}, {Namespace})";

    internal unsafe Bookmark? Commit(RelateTransaction transaction) => Exclusive(() =>
    {
        EnsureCurrent(transaction);
        nint bookmark = 0;
        nint err = 0;
        RelateStatus status = relate_conn_commit(_handle, &bookmark, &err);
        transaction.MarkCompleted();
        _transaction = null;
        string? text = Native.TakeString(bookmark);
        if (status != RelateStatus.Ok)
        {
            // Make sure no half-open transaction survives a failed commit.
            relate_conn_rollback(_handle, null);
        }

        Native.Check(status, err, "commit");
        return text is not null && Bookmark.TryParse(text, out Bookmark? b) ? b : null;
    });

    internal Task<Bookmark?> CommitAsync(RelateTransaction transaction, CancellationToken cancellationToken) =>
        Task.Run(() => Commit(transaction), cancellationToken);

    internal unsafe void Rollback(RelateTransaction transaction) => Exclusive(() =>
    {
        EnsureCurrent(transaction);
        nint err = 0;
        RelateStatus status = relate_conn_rollback(_handle, &err);
        transaction.MarkCompleted();
        _transaction = null;
        Native.Check(status, err, "rollback");
        return true;
    });

    internal Task RollbackAsync(RelateTransaction transaction, CancellationToken cancellationToken) =>
        Task.Run(() => Rollback(transaction), cancellationToken);

    private unsafe RelateTransaction BeginCore()
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open on this connection; commit or roll it back first.");
        }

        nint err = 0;
        Native.Check(relate_conn_begin(_handle, &err), err, "begin");
        _transaction = new RelateTransaction(this);
        return _transaction;
    }

    private unsafe QueryResult QueryCore(string cypher, object? parameters)
    {
        nint builder = ParameterWriter.Build(parameters);
        try
        {
            nint err = 0;
            RelateStatus status = relate_query_params(_handle, cypher, builder, out nint result, &err);
            Native.Check(status, err, "query");
            return QueryResult.FromNative(result);
        }
        finally
        {
            if (builder != 0)
            {
                relate_params_free(builder);
            }
        }
    }

    private void EnsureCurrent(RelateTransaction transaction)
    {
        if (!ReferenceEquals(_transaction, transaction))
        {
            throw new InvalidOperationException("The transaction has already been committed or rolled back.");
        }
    }

    private T Exclusive<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ExclusiveAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
