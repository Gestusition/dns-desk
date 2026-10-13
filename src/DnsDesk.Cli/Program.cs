using System.Text;
using System.Text.Json;
using DnsDesk.Core;
using DnsDesk.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
bool json = args.Contains("--json");
try
{
    if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("DNS Desk requires Windows with DNS-over-HTTPS support.");
    var options = CommandLine.Parse(args);
    if (options.Command == "help") { PrintHelp(); return 0; }
    if (options.Command == "version") { Console.WriteLine("DNS Desk CLI 1.0.0"); return 0; }
    if (options.Command == "providers")
    {
        if (json) PrintJson(DnsSettings.Providers);
        else
        {
            Heading();
            Console.WriteLine($"{"PROVIDER",-14} {"PREFERRED",-18} {"ALTERNATE",-18} HTTPS ENDPOINT");
            foreach (var provider in DnsSettings.Providers) Console.WriteLine($"{provider.Name,-14} {provider.Primary,-18} {provider.Secondary,-18} {provider.Template}");
        }
        return 0;
    }
    var backend = new PowerShellBackend();
    var read = await backend.RunAsync("Read");
    if (!read.Success || read.Snapshot is null) throw new InvalidOperationException(read.Message);
    var snapshot = read.Snapshot;
    if (options.Command == "interactive") options = Interactive(snapshot);
    if (options.Command == "status")
    {
        if (json) PrintJson(snapshot);
        else PrintStatus(snapshot);
        return 0;
    }
    if (options.Command == "doh")
    {
        var match = DnsSettings.FindTemplate(options.Address!, snapshot.DohServers);
        if (json) PrintJson(new { Address = options.Address, Found = match is not null, match?.Template, match?.Source });
        else Console.WriteLine(match is null ? $"No known HTTPS template for {options.Address}. Supply --template when setting this DNS server." : $"{options.Address}\n  HTTPS:  {match.Template}\n  Source: {match.Source}");
        return match is null ? 1 : 0;
    }
    var request = CommandLine.BuildRequest(options, snapshot);
    var adapters = snapshot.Adapters.Where(a => request.AdapterIndexes.Contains(a.Index)).Select(a => a.Name).ToArray();
    if (options.Has("dry-run"))
    {
        if (json) PrintJson(new { DryRun = true, Adapters = adapters, Settings = request });
        else { PrintPlan(request, snapshot); Console.WriteLine("\nDry run. No DNS settings or cache were changed."); }
        return 0;
    }
    if (!options.Has("yes"))
    {
        if (json || Console.IsInputRedirected) throw new ArgumentException("Use --yes to apply without an interactive confirmation, or --dry-run to preview.");
        PrintPlan(request, snapshot);
        Console.Write("\nApply these settings? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled. No DNS settings were changed.");
            return 0;
        }
    }
    if (!json) Console.WriteLine("Saving and verifying DNS settings. Approve administrator access if prompted…");
    var result = await backend.RunAsync("Apply", request);
    if (json) PrintJson(result);
    else
    {
        Console.WriteLine(result.Success ? $"\nOK  {result.Message}" : $"\nERROR  {result.Message}");
        foreach (string log in result.Logs) Console.WriteLine("  " + log);
    }
    return result.Success ? 0 : 1;
}
catch (Exception error)
{
    if (json) PrintJson(new { Success = false, Message = error.Message });
    else Console.Error.WriteLine($"Error: {error.Message}\nRun dns-desk help for usage.");
    return error is ArgumentException ? 2 : 1;
}

static void PrintJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, DnsSettings.Json));
static void Heading() => Console.WriteLine("\nDNS DESK  /  Windows DNS & HTTPS\n");
static void PrintStatus(Snapshot snapshot)
{
    Heading();
    Console.WriteLine($"Engine: {snapshot.Engine}\n");
    foreach (var adapter in snapshot.Adapters)
    {
        Console.WriteLine($"[{adapter.Index}] {adapter.Name}  /  {adapter.Status}  /  {(adapter.Automatic ? "Automatic" : "Manual")} IPv4 DNS");
        foreach (string address in adapter.Servers)
        {
            var known = snapshot.DohServers.FirstOrDefault(s => s.ServerAddress == address);
            string encryption = known?.AutoUpgrade == true ? $"DoH on, fallback {(known.AllowFallbackToUdp ? "on" : "off")}" : "DoH off";
            Console.WriteLine($"    {address,-18} {encryption}");
            if (known?.AutoUpgrade == true) Console.WriteLine($"    HTTPS: {known.DohTemplate}");
        }
        Console.WriteLine();
    }
    if (snapshot.Adapters.Length == 0) Console.WriteLine("No physical Wi-Fi or Ethernet adapters found.");
    Console.WriteLine("IPv6 and IP address settings are preserved.");
}

static void PrintPlan(SettingsRequest request, Snapshot snapshot)
{
    Heading();
    Console.WriteLine("Adapters: " + string.Join(", ", snapshot.Adapters.Where(a => request.AdapterIndexes.Contains(a.Index)).Select(a => a.Name)));
    if (request.Automatic) Console.WriteLine("DNS:      Automatic (DHCP)");
    else
    {
        Console.WriteLine($"DoH:      {(request.UseDoh ? "on" : "off")} / plaintext fallback: {(request.AllowFallback ? "on" : "off")}");
        foreach (var resolver in request.Resolvers)
        {
            Console.WriteLine($"DNS:      {resolver.Address}");
            if (request.UseDoh)
            {
                var match = DnsSettings.FindTemplate(resolver.Address, snapshot.DohServers);
                string source = match?.Template == resolver.Template ? match.Source : "Manual endpoint";
                Console.WriteLine($"HTTPS:    {resolver.Template} ({source})");
            }
        }
        Console.WriteLine("\nDoH preferences for the same DNS server are shared across adapters by Windows.");
    }
    Console.WriteLine("After saving: verify DNS settings, then run ipconfig /flushdns.");
}

static CliOptions Interactive(Snapshot snapshot)
{
    if (Console.IsInputRedirected) throw new ArgumentException("Interactive setup requires a terminal. Use set with explicit options instead.");
    PrintStatus(snapshot);
    Console.WriteLine("1  Google       8.8.8.8 / 8.8.4.4\n2  Cloudflare   1.1.1.1 / 1.0.0.1\n3  Quad9        9.9.9.9 / 149.112.112.112\n4  Custom DNS\n5  Restore automatic DNS\n");
    Console.Write("Choose [1]: ");
    string choice = Console.ReadLine()?.Trim() ?? "";
    if (choice.Length == 0) choice = "1";
    if (choice is not ("1" or "2" or "3" or "4" or "5")) throw new ArgumentException("Choose a number from 1 to 5.");
    Console.Write("Adapters: all, wifi, ethernet, name, or index [all]: ");
    string selectors = Console.ReadLine()?.Trim() ?? "";
    if (selectors.Length == 0) selectors = "all";
    if (choice == "5") return CommandLine.Parse(["reset", "--adapters", selectors]);
    var arguments = new List<string> { "set", "--adapters", selectors };
    string[] addresses;
    if (choice == "4")
    {
        Console.Write("Preferred IPv4 DNS: ");
        string primary = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Alternate IPv4 DNS (optional): ");
        string secondary = Console.ReadLine()?.Trim() ?? "";
        addresses = secondary.Length == 0 ? [primary] : [primary, secondary];
        arguments.AddRange(["--dns", string.Join(",", addresses)]);
    }
    else
    {
        var provider = DnsSettings.Providers[int.Parse(choice) - 1];
        addresses = [provider.Primary, provider.Secondary];
        arguments.AddRange(["--provider", provider.Name.ToLowerInvariant()]);
    }
    Console.Write("Use DNS over HTTPS? [Y/n]: ");
    bool doh = !string.Equals(Console.ReadLine()?.Trim(), "n", StringComparison.OrdinalIgnoreCase);
    arguments.AddRange(["--doh", doh ? "on" : "off"]);
    if (doh)
    {
        for (int i = 0; i < addresses.Length; i++)
        {
            if (DnsSettings.FindTemplate(addresses[i], snapshot.DohServers) is { } match)
            {
                if (i == 1 && arguments.Contains("--template")) arguments.AddRange(["--secondary-template", match.Template]);
                continue;
            }
            Console.Write($"HTTPS endpoint for {addresses[i]}: ");
            arguments.AddRange([i == 0 ? "--template" : "--secondary-template", Console.ReadLine()?.Trim() ?? ""]);
        }
        Console.Write("Allow plaintext fallback? [y/N]: ");
        arguments.AddRange(["--fallback", string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ? "on" : "off"]);
    }
    return CommandLine.Parse(arguments.ToArray());
}

static void PrintHelp() => Console.WriteLine("""
DNS Desk CLI 1.0.0 — Windows DNS settings from your terminal

Usage:
  dns-desk                         Interactive setup
  dns-desk status [--json]          Show physical adapters and IPv4 DNS
  dns-desk providers [--json]       List built-in provider presets
  dns-desk doh <IPv4> [--json]      Find an HTTPS template
  dns-desk set --provider <name> [options]
  dns-desk set --dns <IPv4[,IPv4]> [options]
  dns-desk reset [--adapters <selection>] [--yes] [--dry-run] [--json]

Set options:
  --adapters <selection>            all (default), wifi, ethernet, names, indexes
  --doh on|off                     Enable HTTPS (default: on)
  --fallback on|off                Plaintext fallback (default: off)
  --template <https://...>          Override template; used for both addresses
  --secondary-template <https://...> Override the alternate server's template
  --dry-run                        Preview without changing DNS or requesting admin
  --yes                            Apply without interactive confirmation
  --json                           JSON output; applying also requires --yes
  --no-color                       Accepted for compatibility; output is plain text

Examples:
  dns-desk set --provider google --adapters all --dry-run
  dns-desk set --provider cloudflare --adapters wifi --doh on --yes
  dns-desk set --dns 192.0.2.1 --doh on --template https://resolver.example/dns-query --dry-run
  dns-desk reset --adapters all --yes

PowerShell 7 is preferred; Windows PowerShell is the fallback.
Only applying requests administrator access. Saving also runs ipconfig /flushdns.
IPv6 and IP address settings are preserved.
""");
