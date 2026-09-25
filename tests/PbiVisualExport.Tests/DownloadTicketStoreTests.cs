namespace PbiVisualExport.Tests;

public class DownloadTicketStoreTests
{
    private static readonly ExportTarget Target = new(Fixtures.NewRequest(), Guid.NewGuid(), "ws", Guid.NewGuid(), "ds", Guid.NewGuid(), "rpt");

    private static DownloadTicketStore Store(ManualTime time, int lifetimeSeconds = 60, int retentionMinutes = 1) =>
        new(TestOptions.Create(o =>
        {
            o.Downloads.TicketLifetimeSeconds = lifetimeSeconds;
            o.Downloads.StatusRetentionMinutes = retentionMinutes;
        }), time);

    private static DownloadTicket Issue(DownloadTicketStore store) => store.Issue(Target, null, "user:u", "U", "f.csv");

    [Fact]
    public void Issue_CreatesAnUnguessableTicketThatExpiresAfterTheLifetime()
    {
        var time = new ManualTime();
        var store = Store(time, lifetimeSeconds: 30);
        var a = Issue(store);
        var b = Issue(store);

        Assert.Equal(43, a.Id.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(time.Now.AddSeconds(30), a.ExpiresAt);
        Assert.Equal(DownloadState.Issued, a.State);
        Assert.Same(a, store.Find(a.Id));
        Assert.Null(store.Find("nope"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Lifetime_IsAtLeastOneSecond(int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(1), Store(new ManualTime(), lifetimeSeconds: seconds).Lifetime);

    [Fact]
    public void Redeem_WorksOnce()
    {
        var t = Issue(Store(new ManualTime()));
        Assert.True(t.TryRedeem());
        Assert.Equal(DownloadState.Streaming, t.State);
        Assert.False(t.TryRedeem());
    }

    [Fact]
    public void Find_ExpiresAnUnusedTicketPastItsLifetime()
    {
        var time = new ManualTime();
        var store = Store(time, lifetimeSeconds: 60);
        var t = Issue(store);

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(DownloadState.Issued, store.Find(t.Id)!.State);

        time.Advance(TimeSpan.FromSeconds(1));
        var found = store.Find(t.Id)!;
        Assert.Equal(DownloadState.Expired, found.State);
        Assert.Equal("The download link expired before it was used.", found.Error);
        Assert.False(found.TryRedeem());
    }

    [Fact]
    public void Finish_RecordsTheOutcome()
    {
        var t = Issue(Store(new ManualTime()));
        t.TryRedeem();
        t.Finish(DownloadState.Failed, "boom");
        Assert.Equal(DownloadState.Failed, t.State);
        Assert.Equal("boom", t.Error);
        Assert.NotNull(t.FinishedAt);
    }

    [Fact]
    public void Sweep_RemovesOldTickets_AtMostOncePerMinute()
    {
        var time = new ManualTime();
        var store = Store(time, lifetimeSeconds: 60, retentionMinutes: 1);
        var t0 = time.Now;

        var x = Issue(store);                         // T0: sweeps (nothing to remove)
        time.Now = t0.AddSeconds(30);
        var y = Issue(store);                         // throttled
        time.Now = t0.AddSeconds(150);
        Issue(store);                                 // sweeps: x expired at T0+60s, retained until T0+120s
        Assert.Null(store.Find(x.Id));
        Assert.NotNull(store.Find(y.Id));             // y expired at T0+90s, retained until T0+150s

        time.Now = t0.AddSeconds(180);
        Issue(store);                                 // throttled: y is past retention but still there
        Assert.Equal(DownloadState.Expired, store.Find(y.Id)!.State); // now finished (expired) at T0+180s

        time.Now = t0.AddSeconds(241);
        Issue(store);                                 // sweeps again: y retained until T0+240s
        Assert.Null(store.Find(y.Id));
    }
}

public class ExportConcurrencyLimiterTests
{
    private static ExportConcurrencyLimiter Limiter(int perUser, int total) =>
        new(TestOptions.Create(o =>
        {
            o.Export.MaxConcurrentExportsPerUser = perUser;
            o.Export.MaxConcurrentExports = total;
        }));

    [Fact]
    public void PerUserCap()
    {
        var limiter = Limiter(perUser: 2, total: 0);
        var a = limiter.TryAcquire("u1");
        var b = limiter.TryAcquire("u1");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.False(limiter.WouldAllow("u1"));
        Assert.Null(limiter.TryAcquire("u1"));
        Assert.NotNull(limiter.TryAcquire("u2"));

        a!.Dispose();
        Assert.True(limiter.WouldAllow("u1"));
        Assert.NotNull(limiter.TryAcquire("u1"));
    }

    [Fact]
    public void TotalCap()
    {
        var limiter = Limiter(perUser: 0, total: 2);
        Assert.NotNull(limiter.TryAcquire("u1"));
        Assert.NotNull(limiter.TryAcquire("u2"));
        Assert.False(limiter.WouldAllow("u3"));
        Assert.Null(limiter.TryAcquire("u3"));
    }

    [Fact]
    public void ZeroMeansUnlimited()
    {
        var limiter = Limiter(perUser: 0, total: 0);
        for (int i = 0; i < 100; i++) Assert.NotNull(limiter.TryAcquire("u"));
    }

    [Fact]
    public void LeaseDispose_IsIdempotent()
    {
        var limiter = Limiter(perUser: 0, total: 1);
        var lease = limiter.TryAcquire("u")!;
        lease.Dispose();
        lease.Dispose();
        Assert.NotNull(limiter.TryAcquire("u"));
        Assert.Null(limiter.TryAcquire("u"));
    }
}
