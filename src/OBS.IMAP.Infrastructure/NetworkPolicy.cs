using System.Net;
using System.Net.Sockets;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

public static class NetworkPolicy
{
    private static readonly IPNetwork[] Denied = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
        "192.88.99.0/24", "224.0.0.0/4", "240.0.0.0/4", "::/96", "::1/128", "64:ff9b::/96", "64:ff9b:1::/48", "100::/64",
        "2001::/23", "2001:db8::/32", "2002::/16", "fc00::/7", "fe80::/10", "fec0::/10", "ff00::/8"
    }.Select(IPNetwork.Parse).ToArray();

    public static bool IsAllowed(IPAddress address, IReadOnlyList<string> explicitNetworks)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        // Explicit exceptions enable private/on-premises test mail servers, scoped to the configured account.
        if (explicitNetworks.Any(network => IPNetwork.Parse(network).Contains(address))) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !IPNetwork.Parse("2000::/3").Contains(address)) return false;
        return !Denied.Any(network => network.Contains(address));
    }

    public static Task<Socket> ConnectAsync(AccountOptions account, CancellationToken cancellationToken) =>
        ConnectAsync(account.Host, account.Port, account.AllowedPrivateNetworks, cancellationToken);

    internal static async Task<Socket> ConnectAsync(string host, int port, IReadOnlyList<string> allowedPrivateNetworks,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsAllowed(address, allowedPrivateNetworks)))
            throw new ConnectorException(ErrorCodes.AccessDenied);
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(address, port, cancellationToken); return socket; }
            catch (OperationCanceledException) { socket.Dispose(); throw; }
            catch (SocketException) { socket.Dispose(); }
        }
        throw new ConnectorException(ErrorCodes.ProviderUnavailable);
    }
}
