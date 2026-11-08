using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class UidPagerTests
{
    [Fact]
    public async Task More_than_100000_messages_page_without_duplicates_or_full_mailbox_materialization()
    {
        var limits = new ConnectorLimits();
        var calls = 0;
        Task<IReadOnlyList<uint>> Search(uint low, uint high, CancellationToken token)
        {
            calls++;
            Assert.InRange(high - low + 1, 1u, 10_000u);
            return Task.FromResult<IReadOnlyList<uint>>(Enumerable.Range((int)low, (int)(high - low + 1)).Select(value => (uint)value).ToArray());
        }
        var first = await UidPager.ReadAsync(150_000, 200, limits, Search, default);
        Assert.Equal(150_000u, first.Uids[0]);
        Assert.Equal(149_801u, first.Uids[^1]);
        var second = await UidPager.ReadAsync(first.Next, 200, limits, Search, default);
        Assert.Equal(149_800u, second.Uids[0]);
        Assert.Empty(first.Uids.Intersect(second.Uids));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Sparse_intervals_can_return_empty_page_with_more_work_and_resume_after_expunge()
    {
        var limits = new ConnectorLimits { MaxSearchIntervals = 2 };
        var mailbox = new HashSet<uint> { 1, 17, 100_001, 250_000 };
        Task<IReadOnlyList<uint>> Search(uint low, uint high, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<uint>>(mailbox.Where(uid => uid >= low && uid <= high).ToArray());
        var first = await UidPager.ReadAsync(250_000, 50, limits, Search, default);
        Assert.Equal([250_000u], first.Uids);
        Assert.Equal(230_000u, first.Next);
        mailbox.Remove(100_001); // Expunged messages must not be fabricated or retrieved by sequence number.
        var empty = await UidPager.ReadAsync(first.Next, 50, limits, Search, default);
        Assert.Empty(empty.Uids);
        Assert.True(empty.Next > 0);
        var next = empty.Next;
        var seen = new List<uint>();
        while (next > 0)
        {
            var page = await UidPager.ReadAsync(next, 50, limits, Search, default);
            seen.AddRange(page.Uids);
            Assert.True(page.Next < next);
            next = page.Next;
        }
        Assert.Equal([17u, 1u], seen);
    }

    [Fact]
    public async Task Server_cannot_return_out_of_window_or_unbounded_uid_list()
    {
        Assert.Equal(ErrorCodes.LimitExceeded, (await Assert.ThrowsAsync<ConnectorException>(() => UidPager.ReadAsync(100,
            50, new(), (_, _, _) => Task.FromResult<IReadOnlyList<uint>>([101]), default))).Code);
    }
}
