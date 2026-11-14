namespace Relate.Net.Tests;

public class ConcurrencyAndAsyncTests
{
    [Fact]
    public async Task QueryAsync_ReturnsResult()
    {
        using var t = new TestDb();
        var result = await t.Connection.QueryAsync("RETURN $x * 2 AS y", new { x = 21 });

        Assert.Equal(42L, result.Scalar<long>());
    }

    [Fact]
    public async Task QueryAsync_WithCanceledToken_DoesNotRun()
    {
        using var t = new TestDb();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Connection.QueryAsync("CREATE (:Never)", cancellationToken: cts.Token));

        Assert.Equal(0L, t.Connection.Query("MATCH (n:Never) RETURN count(n)").Scalar<long>());
    }

    [Fact]
    public async Task SharedConnection_SerializesConcurrentCallers()
    {
        using var t = new TestDb();

        var tasks = Enumerable.Range(0, 64)
            .Select(i => i % 2 == 0
                ? Task.Run(() => t.Connection.Query("CREATE (:C {i: $i}) RETURN $i AS i", new { i }).Scalar<long>())
                : t.Connection.QueryAsync("CREATE (:C {i: $i}) RETURN $i AS i", new { i }).ContinueWith(r => r.Result.Scalar<long>()))
            .ToArray();

        long[] results = await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, 64).Sum(), results.Sum());
        Assert.Equal(64L, t.Connection.Query("MATCH (c:C) RETURN count(c)").Scalar<long>());
    }

    [Fact]
    public async Task SeparateConnections_RunInParallel()
    {
        using var db = RelateDatabase.OpenInMemory();

        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            using var conn = db.Connect();
            for (int i = 0; i < 25; i++)
            {
                conn.Query("CREATE (:W {worker: $w, i: $i})", new { w, i });
            }
        }));

        await Task.WhenAll(workers);

        using var check = db.Connect();
        Assert.Equal(200L, check.Query("MATCH (n:W) RETURN count(n)").Scalar<long>());
    }

    [Fact]
    public async Task DisposeAsync_ClosesConnection()
    {
        using var db = RelateDatabase.OpenInMemory();
        var conn = db.Connect();

        await conn.DisposeAsync();

        Assert.True(conn.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => conn.QueryAsync("RETURN 1"));
    }
}
