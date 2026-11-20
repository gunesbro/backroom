using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace Backroom.BuildingBlocks.Presentation.RateLimiting;

/// <summary>
/// The rate limiting key of a request's client network: an IPv4 address, or an IPv6 /64 network (a host can rotate
/// addresses within its /64). Behind a reverse proxy, the deployment must configure trusted forwarded headers. The key
/// is personal data: it is used only in memory and never logged.
/// </summary>
public static class ClientNetwork
{
    public static string PartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes).ToString();
        }

        return address.ToString();
    }
}
