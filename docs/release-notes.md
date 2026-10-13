DNS Desk CLI configures IPv4 DNS for Wi-Fi and Ethernet from your Windows terminal.

This release includes provider presets, Windows-first DoH template lookup, custom HTTPS endpoints, adapter selection, interactive setup, JSON output, dry-run previews, automatic DNS restoration, and cache flushing after verified saves.

Download `dns-desk.exe` and run it from PowerShell or Windows Terminal. The executable is self-contained. PowerShell 7 is preferred, with Windows PowerShell as the fallback. Administrator access is requested only when applying settings.

Use `dns-desk help` for commands or `dns-desk set --provider google --dry-run` for a preview.

IPv6 settings are preserved. DoH definitions are shared per resolver address by Windows. Read-only integration and simulated rollback checks cover the release; live adapter mutation through UAC is not part of automated verification.
