using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DnsDesk.Core;

public sealed record Provider(string Name, string Primary, string Secondary, string Template);
public sealed record DohServer(string ServerAddress, string DohTemplate, bool AutoUpgrade, bool AllowFallbackToUdp);
public sealed record Adapter(int Index, string Name, string Status, string Kind, string[] Servers, bool Automatic);
public sealed record Snapshot(Adapter[] Adapters, DohServer[] DohServers, string Engine);
public sealed record Resolver(string Address, string Template);
public sealed record SettingsRequest(int[] AdapterIndexes, Resolver[] Resolvers, bool UseDoh, bool AllowFallback, bool Automatic);
public sealed record BackendResponse(bool Success, string Message, string[] Logs, Snapshot? Snapshot);
public sealed record TemplateMatch(string Template, string Source);

public static class DnsSettings
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static readonly Provider[] Providers =
    [
        new("Google", "8.8.8.8", "8.8.4.4", "https://dns.google/dns-query"),
        new("Cloudflare", "1.1.1.1", "1.0.0.1", "https://cloudflare-dns.com/dns-query"),
        new("Quad9", "9.9.9.9", "149.112.112.112", "https://dns.quad9.net/dns-query")
    ];

    public static bool IsIpv4(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
        ip.ToString() == address && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast) &&
        address.Split('.').Length == 4;

    public static bool IsHttpsTemplate(string template) =>
        Uri.TryCreate(template, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public static TemplateMatch? FindTemplate(string address, IEnumerable<DohServer> windows)
    {
        var registered = windows.FirstOrDefault(s => s.ServerAddress == address && IsHttpsTemplate(s.DohTemplate));
        if (registered is not null) return new(registered.DohTemplate, "Windows registry");
        var provider = Providers.FirstOrDefault(p => p.Primary == address || p.Secondary == address);
        return provider is null ? null : new(provider.Template, $"{provider.Name} catalog");
    }

    public static string? Validate(SettingsRequest request)
    {
        if (request.AdapterIndexes.Length == 0) return "Select at least one network adapter.";
        if (request.AdapterIndexes.Any(i => i <= 0) || request.AdapterIndexes.Distinct().Count() != request.AdapterIndexes.Length)
            return "The adapter selection is invalid. Refresh and select the adapters again.";
        if (request.Automatic) return null;
        if (request.Resolvers.Length is < 1 or > 2) return "Enter a preferred DNS address and, optionally, an alternate address.";
        if (request.Resolvers.Any(r => !IsIpv4(r.Address))) return "Enter full IPv4 DNS addresses, such as 8.8.8.8.";
        if (request.Resolvers.Select(r => r.Address).Distinct().Count() != request.Resolvers.Length)
            return "Preferred and alternate DNS must use different addresses.";
        if (request.UseDoh && request.Resolvers.Any(r => !IsHttpsTemplate(r.Template)))
            return "Each DNS address needs a valid HTTPS endpoint when DNS over HTTPS is enabled.";
        return null;
    }
}
