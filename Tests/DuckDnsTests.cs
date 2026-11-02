using CapyServer.Core;
using System.Net;

public static class DuckDnsTests
{
    public static async Task Run()
    {
        Check(DuckDnsName.Parse(" Capy-Friends.DuckDNS.org ").Hostname == "capy-friends.duckdns.org", "Names normalize to a single DuckDNS label.");
        foreach (var bad in new[] { "https://foo.duckdns.org", "foo,bar", "a.b", "-foo", "foo-", "foo.duckdns.org.evil.org", new string('a', 64), "foo\nbar" })
            Throws<InvalidDataException>(() => DuckDnsName.Parse(bad));
        Check(JoinAddress.Parse("capy-friends.duckdns.org").Port == 19800 && JoinAddress.Parse("capy-friends.duckdns.org").GameAddress == "capy-friends.duckdns.org", "Bare hostnames use the native default port.");
        Check(JoinAddress.Parse("host.example:19801").GameAddress == "host.example:19801", "Manual non-default ports remain explicit.");
        const string secret = "synthetic-token-&-not-a-real-credential";
        using var handler = new Handler(); using var http = new HttpClient(handler); var dns = new DuckDnsClient(http); var name = DuckDnsName.Parse("capy-friends");
        var updated = await dns.UpdateAsync(name, secret, IPAddress.Parse("8.8.8.8"));
        Check(updated.Hostname == name.Hostname && updated.Address.Equals(IPAddress.Parse("8.8.8.8")), "Provider acknowledgment confirms hostname and public IPv4.");
        Check(handler.Last!.Scheme == "https" && handler.Last.Host == "www.duckdns.org" && handler.Last.Query.Contains("synthetic-token-%26-not-a-real-credential"), "HTTPS and escaped account credentials.");
        handler.Body = "KO\n";
        try { await dns.UpdateAsync(name, secret); throw new Exception("KO must fail"); } catch (IOException ex) { Check(!ex.Message.Contains(secret), "Failure messages do not expose credentials."); }
        handler.Body = "OK\n10.0.0.1\n\nUPDATED\n"; await ThrowsAsync<IOException>(() => dns.UpdateAsync(name, secret));
        handler.Body = "OK\n1.1.1.1\n\nUPDATED\n"; await ThrowsAsync<IOException>(() => dns.UpdateAsync(name, secret, IPAddress.Parse("8.8.8.8")));
        handler.Body = new string('x', 5000); await ThrowsAsync<IOException>(() => dns.UpdateAsync(name, secret));
        handler.Body = "OK\n8.8.8.8\n\nNOCHANGE\n"; using var canceled = new CancellationTokenSource(); canceled.Cancel(); await ThrowsAsync<OperationCanceledException>(() => dns.UpdateAsync(name, secret, ct: canceled.Token));
        await ThrowsAsync<InvalidDataException>(() => dns.UpdateAsync(name, secret, IPAddress.Loopback));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string Body = "OK\n8.8.8.8\n\nUPDATED\n";
        public Uri? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Last = request.RequestUri; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) }); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
