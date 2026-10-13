using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DnsDesk.Core;

namespace DnsDesk.Cli;

public sealed class PowerShellBackend
{
    public static string FindEngine(bool forceFallback = false)
    {
        if (!forceFallback)
        {
            foreach (string variable in new[] { "ProgramW6432", "ProgramFiles" })
            {
                string? directory = Environment.GetEnvironmentVariable(variable);
                if (directory is null) continue;
                string candidate = Path.Combine(directory, "PowerShell", "7", "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim().Trim('"'), "pwsh.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string ReadScript()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DnsDesk.Cli.Backend.ps1")
            ?? throw new InvalidOperationException("The embedded DNS backend is missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public async Task<BackendResponse> RunAsync(string mode, SettingsRequest? request = null, bool forceFallback = false)
    {
        if (mode is not ("Read" or "Apply")) throw new ArgumentException("Unknown backend operation.");
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, DnsSettings.Json)));
        using var identity = WindowsIdentity.GetCurrent();
        if (mode == "Apply" && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return await RunElevatedAsync(payload);
        string bootstrap = $"& ([scriptblock]::Create([Console]::In.ReadToEnd())) -Mode '{mode}' -Payload '{payload}'";
        var start = new ProcessStartInfo(FindEngine(forceFallback))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        AddArguments(start, bootstrap);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("PowerShell could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(ReadScript());
        process.StandardInput.Close();
        try
        {
            if (mode == "Read") await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            else await process.WaitForExitAsync();
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Windows took too long to return the network settings.");
        }
        return Parse(await outputTask, await errorTask);
    }

    private static void AddArguments(ProcessStartInfo start, string bootstrap)
    {
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)) })
            start.ArgumentList.Add(argument);
    }

    private static BackendResponse Parse(string output, string errors = "")
    {
        try { return JsonSerializer.Deserialize<BackendResponse>(output.Trim(), DnsSettings.Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException($"Windows did not return a valid DNS result. {errors.Trim()}"); }
    }

    private static async Task<BackendResponse> RunElevatedAsync(string payload)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dns-desk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string resultPath = Path.Combine(directory, "result.json");
        try
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                gzip.Write(Encoding.UTF8.GetBytes(ReadScript()));
            string code = Convert.ToBase64String(compressed.ToArray());
            string path = Convert.ToBase64String(Encoding.UTF8.GetBytes(resultPath));
            string bootstrap = $$"""
                $ErrorActionPreference = 'Stop'
                $resultPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{path}}'))
                try {
                    $memory = New-Object IO.MemoryStream(,[Convert]::FromBase64String('{{code}}'))
                    $gzip = New-Object IO.Compression.GZipStream($memory,[IO.Compression.CompressionMode]::Decompress)
                    $reader = New-Object IO.StreamReader($gzip,[Text.Encoding]::UTF8)
                    $script = $reader.ReadToEnd()
                    $reader.Dispose()
                    $memory.Dispose()
                    $reply = & ([scriptblock]::Create($script)) -Mode Apply -Payload '{{payload}}'
                } catch {
                    $reply = [pscustomobject]@{ Success=$false; Message=$_.Exception.Message; Logs=@(); Snapshot=$null } | ConvertTo-Json -Compress
                }
                $file = [IO.File]::Open($resultPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
                try {
                    $bytes = [Text.Encoding]::UTF8.GetBytes([string]$reply)
                    $file.Write($bytes,0,$bytes.Length)
                } finally { $file.Dispose() }
                """;
            var start = new ProcessStartInfo(FindEngine())
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap))
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Administrator PowerShell could not be started.");
            await process.WaitForExitAsync();
            if (!File.Exists(resultPath)) throw new InvalidOperationException("Administrator PowerShell returned no result. Run the command from an administrator terminal.");
            return Parse(await File.ReadAllTextAsync(resultPath));
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new(false, "Administrator access was cancelled. No DNS settings were changed.", [], null);
        }
        finally
        {
            try { if (File.Exists(resultPath)) File.Delete(resultPath); Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
