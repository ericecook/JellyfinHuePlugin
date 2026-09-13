using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    /// <summary>
    /// Finds Hue bridges on the local network with one multicast PTR query per IPv4 interface.
    /// The query carries the QU bit, so bridges answer unicast to this socket's ephemeral port
    /// (verified against a Hue Bridge Pro on 2026-09-13); nothing here binds port 5353, so a
    /// host mDNS daemon is never in the way. The window elapsing is the normal exit. Only the
    /// caller's token cancelling throws; every other failure logs and yields what was found.
    /// </summary>
    public class MdnsBridgeDiscovery
    {
        private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

        private readonly ILogger _logger;

        public MdnsBridgeDiscovery(ILogger logger)
        {
            _logger = logger;
        }

        public virtual async Task<List<HueBridgeDiscovery>> DiscoverAsync(TimeSpan window, CancellationToken cancellationToken)
        {
            var found = new Dictionary<string, HueBridgeDiscovery>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

                var query = MdnsMessage.BuildQuery();
                var sent = 0;
                foreach (var address in LocalIPv4Addresses())
                {
                    try
                    {
                        // Multicast leaves through one interface per send; pick each in turn so a
                        // multi-homed host asks every network it is on.
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        await socket.SendToAsync(query, SocketFlags.None, MulticastEndpoint, cancellationToken);
                        sent++;
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogDebug(ex, "mDNS query on {Interface} failed", address);
                    }
                }

                if (sent == 0)
                {
                    _logger.LogWarning("No network interface could send the mDNS query; falling back to cloud discovery");
                    return new List<HueBridgeDiscovery>();
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(window);
                var buffer = new byte[9000];
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    SocketReceiveFromResult result;
                    try
                    {
                        result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break; // the window elapsed
                    }

                    var sender = ((IPEndPoint)result.RemoteEndPoint).Address;
                    foreach (var bridge in MdnsMessage.ParseResponse(buffer.AsSpan(0, result.ReceivedBytes), sender))
                    {
                        found.TryAdd(bridge.Id, bridge);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local bridge discovery failed; falling back to cloud discovery");
            }

            return found.Values.ToList();
        }

        private static IEnumerable<IPAddress> LocalIPv4Addresses()
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || !nic.SupportsMulticast)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        yield return unicast.Address;
                    }
                }
            }
        }
    }
}
