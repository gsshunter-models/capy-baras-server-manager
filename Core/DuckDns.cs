using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CapyServer.Core;

public sealed record DuckDnsName(string Label)
{
    public string Hostname => Label + ".duckdns.org";
    public static DuckDnsName Parse(string input)
    {
        input = input.Trim().ToLowerInvariant();
        if (input.EndsWith(".duckdns.org", StringComparison.Ordinal)) input = input[..^12];
        if (!Regex.IsMatch(input, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("Enter your DuckDNS name, such as capy-practice or capy-practice.duckdns.org.");
        return new(input);
    }
}
public sealed record DuckDnsUpdate(string Hostname, IPAddress Address);
public sealed class DuckDnsClient(HttpClient client)
{
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    public async Task<DuckDnsUpdate> UpdateAsync(DuckDnsName name, string token, IPAddress? address = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)) throw new InvalidDataException("Save your DuckDNS account token in the setup panel first.");
        if (address != null && !RouterMapping.IsPublic(address)) throw new InvalidDataException("A public IPv4 address is required for the game hostname.");
        var url = "https://www.duckdns.org/update?domains=" + Uri.EscapeDataString(name.Label) + "&token=" + Uri.EscapeDataString(token) + "&verbose=true&ip=" + Uri.EscapeDataString(address?.ToString() ?? "");
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new IOException("DuckDNS could not be reached. Retry automatic setup.");
            using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[1024]; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > 4096) throw new IOException("DuckDNS returned an unexpected response."); output.Write(buffer, 0, count); }
            var lines = Encoding.UTF8.GetString(output.ToArray()).Replace("\r", "").Split('\n');
            if (lines.Length < 2 || lines[0].Trim() != "OK") throw new IOException("DuckDNS did not accept the name/token. Create that name in your DuckDNS account, then save its account token here.");
            if (!IPAddress.TryParse(lines[1].Trim(), out var actual) || !RouterMapping.IsPublic(actual) || address != null && !actual.Equals(address)) throw new IOException("DuckDNS did not confirm the expected public IPv4 address.");
            return new(name.Hostname, actual);
        }
        catch (HttpRequestException) { throw new IOException("DuckDNS could not be reached. Check the internet connection and retry."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("DuckDNS timed out. Retry automatic setup."); }
    }
}
