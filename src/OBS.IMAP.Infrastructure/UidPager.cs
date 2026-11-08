using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

public sealed record UidPage(IReadOnlyList<uint> Uids, uint Next);

public static class UidPager
{
    public static async Task<UidPage> ReadAsync(uint next, int pageSize, ConnectorLimits limits,
        Func<uint, uint, CancellationToken, Task<IReadOnlyList<uint>>> search, CancellationToken cancellationToken)
    {
        var found = new List<uint>();
        for (var interval = 0; next > 0 && interval < limits.MaxSearchIntervals && found.Count < pageSize; interval++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var low = next > limits.UidIntervalWidth ? next - (uint)limits.UidIntervalWidth + 1 : 1;
            var matches = await search(low, next, cancellationToken);
            if (matches.Count > limits.UidIntervalWidth || matches.Any(uid => uid < low || uid > next))
                throw new ConnectorException(ErrorCodes.LimitExceeded);
            var ordered = matches.Distinct().OrderDescending().ToArray();
            var take = ordered.Take(pageSize - found.Count).ToArray();
            found.AddRange(take);
            next = take.Length < ordered.Length ? take[^1] - 1 : low - 1;
        }
        return new UidPage(found, next);
    }
}
