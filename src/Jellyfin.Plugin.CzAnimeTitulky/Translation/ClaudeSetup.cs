using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.CzAnimeTitulky.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Translation;

/// <summary>State of the Claude Code installation/login, for the settings page.</summary>
public sealed record ClaudeStatus(bool Installed, string? Path, string? Version, bool HasToken, string Login, string? LoginUrl, string? Message);

/// <summary>
/// Installs Claude Code and runs "claude setup-token" from the settings page, so neither a server console nor
/// another computer is needed. The long-lived token ends up in the plugin settings and is never shown.
/// </summary>
public sealed partial class ClaudeSetup
{
    private readonly ILogger<ClaudeSetup> _logger;
    private readonly object _lock = new();
    private Process? _login;
    private readonly StringBuilder _output = new();
    private string _state = "idle";
    private string? _url;
    private string? _message;

    public ClaudeSetup(ILogger<ClaudeSetup> logger)
    {
        _logger = logger;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>claude executable: configured path, PATH, or the native installer's ~/.local/bin/claude.</summary>
    public static string? FindClaude()
    {
        var cfg = Plugin.Instance?.Configuration.AiCliPath;
        if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg))
        {
            return cfg;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Append(System.IO.Path.Combine(Home, ".local", "bin")))
        {
            var p = System.IO.Path.Combine(dir, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    public async Task<ClaudeStatus> StatusAsync(CancellationToken ct)
    {
        var path = FindClaude();
        string? version = null;
        if (path is not null)
        {
            try
            {
                version = (await RunAsync(path, "--version", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)).Trim();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                version = "?";
            }
        }

        lock (_lock)
        {
            // only a Claude token counts as signed in (the field may still hold an OpenAI/API key)
            return new ClaudeStatus(path is not null, path, version, OAuthToken().IsMatch(Plugin.Instance?.Configuration.AiToken ?? string.Empty), _state, _url, _message);
        }
    }

    /// <summary>Official native installer (claude.ai/install.sh) into the Jellyfin user's home.</summary>
    public async Task<string> InstallAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return "Na Windows nainstaluj Claude Code ručně (https://claude.com/claude-code) a cestu zadej do nastavení.";
        }

        var output = await RunAsync("/bin/bash", "-c \"curl -fsSL https://claude.ai/install.sh | bash\"", TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        var path = FindClaude();
        ActivityLog.Write(path is null ? "AI        instalace Claude Code selhala" : "AI        Claude Code nainstalován: " + path);
        return path is null ? "Instalace selhala: " + Tail(output) : "Nainstalováno: " + path;
    }

    /// <summary>Starts "claude setup-token" in a pseudo-terminal and returns the sign-in URL.</summary>
    public async Task<ClaudeStatus> StartLoginAsync(CancellationToken ct)
    {
        var claude = FindClaude();
        if (claude is null || OperatingSystem.IsWindows())
        {
            lock (_lock)
            {
                (_state, _message) = ("error", claude is null ? "Claude Code není nainstalovaný." : "Přihlášení z nastavení funguje jen na Linuxu – na Windows spusť claude setup-token a token vlož ručně.");
            }

            return await StatusAsync(ct).ConfigureAwait(false);
        }

        CancelLogin();
        var psi = new ProcessStartInfo("script")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.CreateTempSubdirectory("czat-login-").FullName,
        };
        // wide terminal so that the token is printed on one line
        foreach (var a in new[] { "-qfc", $"stty cols 2000 rows 50; exec '{claude.Replace("'", "'\\''", StringComparison.Ordinal)}' setup-token", "/dev/null" })
        {
            psi.ArgumentList.Add(a);
        }

        var p = Process.Start(psi) ?? throw new InvalidOperationException("script did not start");
        lock (_lock)
        {
            _login = p;
            _output.Clear();
            (_state, _url, _message) = ("starting", null, null);
        }

        _ = PumpAsync(p, p.StandardOutput);
        _ = PumpAsync(p, p.StandardError);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(15)).ConfigureAwait(false);
            lock (_lock)
            {
                if (_login == p && _state != "done")
                {
                    (_state, _message) = ("error", "Přihlášení vypršelo, zkus to znovu.");
                }
            }

            KillIf(p);
        });

        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (_url is not null || _state == "error" || p.HasExited)
                {
                    break;
                }
            }
        }

        return await StatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sends the code shown by claude.ai after sign-in; waits for the token.</summary>
    public async Task<ClaudeStatus> SubmitCodeAsync(string code, CancellationToken ct)
    {
        Process? p;
        lock (_lock)
        {
            p = _login;
        }

        if (p is null || p.HasExited)
        {
            lock (_lock)
            {
                (_state, _message) = ("error", "Přihlášení neběží, začni znovu.");
            }

            return await StatusAsync(ct).ConfigureAwait(false);
        }

        // a long code arriving in one chunk is taken as a paste, and an Enter inside it is swallowed → send Enter separately
        await p.StandardInput.WriteAsync(code.Trim().AsMemory(), ct).ConfigureAwait(false);
        await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        await Task.Delay(700, ct).ConfigureAwait(false);
        await p.StandardInput.WriteAsync("\r".AsMemory(), ct).ConfigureAwait(false);
        await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            _state = "verifying";
        }

        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (_state is "done" or "error")
                {
                    break;
                }
            }
        }

        lock (_lock)
        {
            if (_state == "verifying")
            {
                (_state, _message) = ("error", "Token se neobjevil – zkontroluj kód a zkus to znovu. " + Tail(Clean(_output.ToString())));
            }
        }

        return await StatusAsync(ct).ConfigureAwait(false);
    }

    public void CancelLogin()
    {
        Process? p;
        lock (_lock)
        {
            p = _login;
            _login = null;
            (_state, _url, _message) = ("idle", null, null);
        }

        KillIf(p);
    }

    private async Task PumpAsync(Process p, StreamReader reader)
    {
        var buf = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
            {
                lock (_lock)
                {
                    if (_login != p)
                    {
                        return;
                    }

                    _output.Append(buf, 0, n);
                    var raw = _output.ToString();
                    if (_url is null && LoginUrl().Match(raw) is { Success: true } m)
                    {
                        (_url, _state) = (m.Value, "waiting-code");
                    }

                    if (OAuthToken().Match(Clean(raw)) is { Success: true } t)
                    {
                        var cfg = Plugin.Instance!.Configuration;
                        cfg.AiToken = t.Value;
                        cfg.AiBackend = "claude-cli";
                        if (cfg.AiModel?.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            cfg.AiModel = string.Empty;
                        }

                        Plugin.Instance.SaveConfiguration();
                        (_state, _message, _url) = ("done", "Přihlášeno – token uložen do nastavení doplňku.", null);
                        ActivityLog.Write("AI        přihlášení ke Claude dokončeno (token uložen)");
                        KillIf(p);
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "claude setup-token output ended");
        }
    }

    private static void KillIf(Process? p)
    {
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<string> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(exe + " did not start");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var o = p.StandardOutput.ReadToEndAsync(cts.Token);
        var e = p.StandardError.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        return await o.ConfigureAwait(false) + await e.ConfigureAwait(false);
    }

    /// <summary>Removes terminal escape sequences (colours, cursor moves, OSC hyperlinks).</summary>
    private static string Clean(string s) => Ansi().Replace(s, string.Empty);

    private static string Tail(string s) => s.Length > 300 ? s[^300..] : s;

    [GeneratedRegex(@"https://claude\.(?:com|ai)/[^\s\x07\x1b""]*oauth/authorize\?[^\s\x07\x1b""]+")]
    private static partial Regex LoginUrl();

    [GeneratedRegex(@"sk-ant-oat\d\d-[A-Za-z0-9_\-]{20,}")]
    private static partial Regex OAuthToken();

    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b[()][0-9A-Za-z]|\x1b[=>]")]
    private static partial Regex Ansi();
}
