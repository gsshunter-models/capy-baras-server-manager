using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Mono.Nat;

namespace CapyServer.Core;

public sealed class RouterMapping : IAsyncDisposable
{
    private INatDevice? device;
    private Mapping? mapping;
    private Task? renew;
    private readonly CancellationTokenSource stop = new();
    public IPAddress? ExternalAddress { get; private set; }
    public int PublicPort => mapping?.PublicPort ?? 0;
    public event Action<string>? Status;
    public async Task OpenAsync(int port, Protocol protocol = Protocol.Udp, CancellationToken ct = default)
    {
        var found = new TaskCompletionSource<INatDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Found(object? sender, DeviceEventArgs e) => found.TrySetResult(e.Device);
        NatUtility.DeviceFound += Found;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try { NatUtility.StartDiscovery(); device = await found.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("No UPnP/NAT-PMP router answered. Use a relay, or forward this port in your router."); }
        finally { NatUtility.DeviceFound -= Found; NatUtility.StopDiscovery(); }
        // Never replace a pre-existing UPnP forwarding rule belonging to another service.
        if (device.NatProtocol == NatProtocol.Upnp)
        {
            var existing = await device.GetAllMappingsAsync().WaitAsync(TimeSpan.FromSeconds(8), ct);
            if (existing.Any(m => m.Protocol == protocol && m.PublicPort == port)) throw new IOException("This router port already has a mapping. Choose another port; the existing mapping was left unchanged.");
        }
        mapping = await device.CreatePortMapAsync(new Mapping(protocol, port, port, 3600, "CapyBaraServer-" + Guid.NewGuid().ToString("N")[..12])).WaitAsync(TimeSpan.FromSeconds(10), ct);
        ExternalAddress = await device.GetExternalIPAsync().WaitAsync(TimeSpan.FromSeconds(6), ct);
        Status?.Invoke(IsPublic(ExternalAddress) ? "Router mapping created. Outside connectivity still needs a friend to test." : "Router has a private/upstream address. A relay or upstream port forwarding is needed.");
        renew = RenewAsync(stop.Token);
    }
    private async Task RenewAsync(CancellationToken ct)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15)); while (await timer.WaitForNextTickAsync(ct)) { mapping = await device!.CreatePortMapAsync(mapping!).WaitAsync(TimeSpan.FromSeconds(10), ct); } }
        catch (Exception ex) when (ex is IOException or MappingException or OperationCanceledException or TimeoutException or SocketException) { if (!ct.IsCancellationRequested) Status?.Invoke("Router mapping renewal failed. Reconnect or use a relay."); }
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false; var b = address.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127);
    }
    public static List<string> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) && !new[] { "radmin", "hamachi", "zerotier", "tailscale" }.Any(v => (n.Name + " " + n.Description).Contains(v, StringComparison.OrdinalIgnoreCase))).SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.")).Select(a => a.Address.ToString()).Distinct().ToList();
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); if (renew != null) try { await renew; } catch (OperationCanceledException) { }
        if (device != null && mapping != null) { try { await device.DeletePortMapAsync(mapping).WaitAsync(TimeSpan.FromSeconds(4)); } catch (Exception ex) when (ex is IOException or MappingException or TimeoutException or SocketException) { Status?.Invoke("Router rule could not be removed; its lease will expire."); } }
        mapping = null;
    }
}
