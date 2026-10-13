using DnsDesk.Core;

int passed = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
    passed++;
}
SettingsRequest Request(Resolver[] resolvers, bool doh = true) => new([7], resolvers, doh, false, false);
var google = new Resolver("8.8.8.8", "https://dns.google/dns-query");
Check("canonical IPv4", DnsSettings.IsIpv4(google.Address));
foreach (string invalid in new[] { "127.1", "8.8.8", "008.8.8.8", "8.8.8.999", "2001:4860::8888", "8.8.8.8; whoami", "0.0.0.0", "255.255.255.255", "" })
    Check($"reject address {invalid}", !DnsSettings.IsIpv4(invalid));
Check("HTTPS template", DnsSettings.IsHttpsTemplate(google.Template));
foreach (string invalid in new[] { "http://dns.google/dns-query", "https://user:password@dns.google/dns-query", "https://dns.google/dns-query#fragment", "file:///tmp/query", "" })
    Check($"reject endpoint {invalid}", !DnsSettings.IsHttpsTemplate(invalid));
Check("known provider lookup", DnsSettings.FindTemplate("1.0.0.1", [])?.Template == "https://cloudflare-dns.com/dns-query");
Check("Windows definitions take precedence", DnsSettings.FindTemplate("8.8.8.8", [new("8.8.8.8", "https://example.org/custom", true, false)])?.Template == "https://example.org/custom");
Check("ignore insecure registered definitions", DnsSettings.FindTemplate("8.8.8.8", [new("8.8.8.8", "http://example.org/query", false, false)])?.Source == "Google catalog");
Check("unknown provider is not guessed", DnsSettings.FindTemplate("192.0.2.1", []) is null);
Check("single DNS allowed", DnsSettings.Validate(Request([google])) is null);
Check("DoH requires each endpoint", DnsSettings.Validate(Request([google, new("8.8.4.4", "")])) is not null);
Check("unencrypted DNS allows no endpoint", DnsSettings.Validate(Request([new("8.8.8.8", "")], false)) is null);
Check("duplicate DNS rejected", DnsSettings.Validate(Request([google, google])) is not null);
Check("no adapters rejected", DnsSettings.Validate(Request([google]) with { AdapterIndexes = [] }) is not null);
Check("duplicate adapters rejected", DnsSettings.Validate(Request([google]) with { AdapterIndexes = [7, 7] }) is not null);
Check("automatic DNS bypasses resolver input", DnsSettings.Validate(Request([]) with { Automatic = true }) is null);
var snapshot = new Snapshot([new(7, "Wi-Fi", "Disconnected", "Wi-Fi", [], false), new(5, "Ethernet", "Up", "Ethernet", [], true)], [], "Test");
SettingsRequest Plan(params string[] arguments) => CommandLine.BuildRequest(CommandLine.Parse(arguments), snapshot);
void Reject(string name, params string[] arguments)
{
    bool rejected = false;
    try { Plan(arguments); } catch (ArgumentException) { rejected = true; }
    Check(name, rejected);
}
Check("interactive default", CommandLine.Parse([]).Command == "interactive");
Check("provider preset selects both adapters", Plan("set", "--provider", "google").AdapterIndexes.Length == 2);
Check("wifi selector selects only Wi-Fi", Plan("set", "--provider", "google", "--adapters", "wifi").AdapterIndexes.SequenceEqual([7]));
Check("adapter names with spaces supported", CommandLine.BuildRequest(CommandLine.Parse(["set", "--provider", "google", "--adapters", "USB Ethernet"]), snapshot with { Adapters = [new(11, "USB Ethernet", "Up", "Ethernet", [], true)] }).AdapterIndexes.SequenceEqual([11]));
Check("numeric selectors deduplicate overlapping adapters", Plan("set", "--provider", "google", "--adapters", "wifi,7,5").AdapterIndexes.SequenceEqual([7, 5]));
Check("DoH is on and plaintext fallback off by default", Plan("set", "--provider", "google") is { UseDoh: true, AllowFallback: false });
Check("manual primary and separate secondary endpoint", Plan("set", "--dns", "192.0.2.1,8.8.4.4", "--template", "https://custom.example/query", "--secondary-template", "https://dns.google/dns-query").Resolvers[1].Template == "https://dns.google/dns-query");
Check("reset has no resolvers or DoH mutation", Plan("reset") is { Automatic: true, UseDoh: false, Resolvers.Length: 0 });
Reject("provider and addresses cannot be combined", "set", "--provider", "google", "--dns", "8.8.8.8");
Reject("set requires a provider or addresses", "set");
Reject("unknown adapter fails before changes", "set", "--provider", "google", "--adapters", "missing");
Reject("unknown provider fails before changes", "set", "--provider", "unknown");
Reject("unknown DoH resolver requires a template", "set", "--dns", "192.0.2.1");
Reject("invalid on/off option rejected", "set", "--provider", "google", "--doh", "maybe");
Reject("fallback cannot be on with DoH off", "set", "--provider", "google", "--doh", "off", "--fallback", "on");
Reject("duplicate options rejected", "set", "--provider", "google", "--provider", "quad9");
Reject("missing option values rejected", "set", "--provider");
Reject("unknown options rejected", "set", "--provider", "google", "--typo");
Reject("reset cannot silently ignore resolver options", "reset", "--provider", "google");
Console.WriteLine($"{passed} core and CLI checks passed.");
