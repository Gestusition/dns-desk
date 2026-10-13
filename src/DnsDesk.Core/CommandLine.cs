namespace DnsDesk.Core;

public sealed record CliOptions(string Command, Dictionary<string, string> Values, HashSet<string> Flags, string? Address)
{
    public string Get(string name, string fallback = "") => Values.GetValueOrDefault(name, fallback);
    public bool Has(string flag) => Flags.Contains(flag);
}

public static class CommandLine
{
    private static readonly HashSet<string> ValueOptions = ["provider", "dns", "adapters", "doh", "fallback", "template", "secondary-template"];
    private static readonly HashSet<string> FlagOptions = ["yes", "dry-run", "json", "no-color"];

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0) return new("interactive", [], [], null);
        if (args[0] is "--help" or "-h" or "help") return new("help", [], [], null);
        if (args[0] is "--version" or "version") return new("version", [], [], null);
        string command = args[0].ToLowerInvariant();
        if (command is not ("status" or "providers" or "doh" or "set" or "reset")) throw new ArgumentException($"Unknown command: {args[0]}");
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        string? address = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (command == "doh" && i == 1 && !args[i].StartsWith("--", StringComparison.Ordinal)) { address = args[i]; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {args[i]}");
            string name = args[i][2..];
            if (FlagOptions.Contains(name))
            {
                if (!flags.Add(name)) throw new ArgumentException($"Duplicate option: --{name}");
            }
            else if (ValueOptions.Contains(name))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"--{name} requires a value.");
                if (!values.TryAdd(name, args[++i])) throw new ArgumentException($"Duplicate option: --{name}");
            }
            else throw new ArgumentException($"Unknown option: --{name}");
        }
        if (command == "doh" && !DnsSettings.IsIpv4(address ?? "")) throw new ArgumentException("doh requires a full IPv4 address.");
        string[] allowed = command switch { "set" => ValueOptions.ToArray(), "reset" => ["adapters"], _ => [] };
        if (values.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException($"One or more options do not apply to {command}.");
        if (command is not ("set" or "reset") && flags.Any(f => f is "yes" or "dry-run")) throw new ArgumentException($"--yes and --dry-run do not apply to {command}.");
        return new(command, values, flags, address);
    }

    public static SettingsRequest BuildRequest(CliOptions options, Snapshot snapshot)
    {
        bool automatic = options.Command == "reset";
        string selector = options.Get("adapters", "all");
        var selected = new List<int>();
        foreach (string raw in selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = snapshot.Adapters.Where(a =>
                raw.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals(a.Name, StringComparison.OrdinalIgnoreCase) ||
                raw.Equals(a.Index.ToString(), StringComparison.Ordinal) ||
                raw.Equals("wifi", StringComparison.OrdinalIgnoreCase) && a.Kind == "Wi-Fi" ||
                raw.Equals("ethernet", StringComparison.OrdinalIgnoreCase) && a.Kind == "Ethernet").ToArray();
            if (matches.Length == 0) throw new ArgumentException($"No physical adapter matches '{raw}'. Run status to see adapters.");
            selected.AddRange(matches.Select(a => a.Index));
        }
        bool doh = ParseSwitch(options.Get("doh", "on"), "doh");
        bool fallback = ParseSwitch(options.Get("fallback", "off"), "fallback");
        if (!doh && fallback) throw new ArgumentException("Plaintext fallback only applies when DoH is on.");
        if (automatic) return Validate(new(selected.Distinct().ToArray(), [], false, false, true));
        bool providerSpecified = options.Values.ContainsKey("provider");
        bool dnsSpecified = options.Values.ContainsKey("dns");
        if (providerSpecified == dnsSpecified) throw new ArgumentException("Choose exactly one of --provider or --dns.");
        string[] addresses;
        if (providerSpecified)
        {
            var provider = DnsSettings.Providers.FirstOrDefault(p => p.Name.Equals(options.Get("provider"), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Unknown provider. Run providers for the available presets.");
            addresses = [provider.Primary, provider.Secondary];
        }
        else addresses = options.Get("dns").Split(',', StringSplitOptions.TrimEntries);
        var resolvers = addresses.Select((address, index) => new Resolver(address,
            index == 1 && options.Values.ContainsKey("secondary-template") ? options.Get("secondary-template") :
            options.Values.ContainsKey("template") ? options.Get("template") :
            DnsSettings.FindTemplate(address, snapshot.DohServers)?.Template ?? "")).ToArray();
        if (options.Values.ContainsKey("secondary-template") && resolvers.Length != 2) throw new ArgumentException("--secondary-template requires an alternate DNS address.");
        return Validate(new(selected.Distinct().ToArray(), resolvers, doh, fallback, false));
    }

    private static bool ParseSwitch(string value, string name) => value.ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException($"--{name} must be on or off.")
    };

    private static SettingsRequest Validate(SettingsRequest request)
    {
        if (DnsSettings.Validate(request) is { } message) throw new ArgumentException(message);
        return request;
    }
}
