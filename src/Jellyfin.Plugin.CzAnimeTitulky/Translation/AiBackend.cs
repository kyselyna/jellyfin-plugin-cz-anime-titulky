using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Translation;

/// <summary>Sends one prompt to the configured AI and returns the reply text.</summary>
public sealed class AiBackend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ILogger<AiBackend> _logger;

    public AiBackend(ILogger<AiBackend> logger)
    {
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    private static readonly object UsageLock = new();
    private static (long In, long Out) _usage;

    /// <summary>Tokens used since the last call (input, output) and resets the counter – 0 for the subscription.</summary>
    public static (long In, long Out) TakeUsage()
    {
        lock (UsageLock)
        {
            var u = _usage;
            _usage = (0, 0);
            return u;
        }
    }

    private static void AddUsage(long i, long o)
    {
        lock (UsageLock)
        {
            _usage = (_usage.In + i, _usage.Out + o);
        }
    }

    /// <summary>Price in USD per 1M tokens (input, output) for cost tracking; unknown models count as the dearest.</summary>
    public static (double In, double Out) Price(string backend, string model)
    {
        if (backend is "claude-cli" or "local")
        {
            return (0, 0);
        }

        var m = model.ToLowerInvariant();
        return m switch
        {
            _ when m.Contains("nano", StringComparison.Ordinal) => (0.05, 0.4),
            _ when m.Contains("mini", StringComparison.Ordinal) => (0.25, 2),
            _ when m.Contains("haiku", StringComparison.Ordinal) => (1, 5),
            _ when m.Contains("sonnet", StringComparison.Ordinal) => (3, 15),
            _ when m.Contains("opus", StringComparison.Ordinal) => (5, 25),
            _ when m.StartsWith("gpt-5", StringComparison.Ordinal) => (1.25, 10),
            _ => (5, 25),
        };
    }

    /// <summary>Model actually used (setting or backend default).</summary>
    public static string Model() => Config.AiBackend switch
    {
        "openai-api" => string.IsNullOrWhiteSpace(Config.AiModel) ? "gpt-5-mini" : Config.AiModel.Trim(),
        "anthropic-api" => string.IsNullOrWhiteSpace(Config.AiModel) ? "claude-sonnet-5-5" : Config.AiModel.Trim(),
        "local" => Config.AiModel.Trim(),
        _ => string.IsNullOrWhiteSpace(Config.AiModel) ? "sonnet" : Config.AiModel.Trim(),
    };

    public string Describe() => Config.AiBackend switch
    {
        "anthropic-api" => "Claude API",
        "openai-api" => "OpenAI API",
        "local" => "lokální AI",
        _ => "Claude (Claude Code)",
    };

    public Task<string> AskAsync(string system, string prompt, CancellationToken ct) => Config.AiBackend switch
    {
        "anthropic-api" => AnthropicApiAsync(system, prompt, ct),
        "openai-api" => OpenAiApiAsync(system, prompt, ct),
        "local" => LocalAsync(system, prompt, ct),
        "claude-cli" => ClaudeCliAsync(system, prompt, ct),
        _ => throw new InvalidOperationException("AI: neznámý překladač " + Config.AiBackend),
    };

    /// <summary>Claude Code in print mode with the claude.ai subscription (token from "claude setup-token").</summary>
    private async Task<string> ClaudeCliAsync(string system, string prompt, CancellationToken ct)
    {
        var cfg = Config;
        // Subtitle text is untrusted: no tools at all (built-in or MCP), no user/project settings or hooks,
        // empty working directory – the model can only answer with text.
        var args = new List<string>
        {
            "-p", "--output-format", "json",
            "--model", Model(),
            "--append-system-prompt", system,
            "--tools", string.Empty,
            "--strict-mcp-config",
            "--setting-sources", string.Empty,
            "--no-session-persistence",
            "--disallowedTools", "Bash", "Edit", "Write", "Read", "WebFetch", "WebSearch", "Glob", "Grep", "Task", "NotebookEdit",
        };
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(cfg.AiToken))
        {
            env["CLAUDE_CODE_OAUTH_TOKEN"] = cfg.AiToken.Trim();
        }

        var exe = ClaudeSetup.FindClaude() ?? throw new InvalidOperationException("Claude Code není nainstalovaný – v nastavení doplňku klikni na Nainstalovat Claude Code.");
        var json = await RunAsync(exe, args, prompt, env, ct).ConfigureAwait(false);
        var node = JsonNode.Parse(json);
        if ((bool?)node?["is_error"] == true)
        {
            throw new InvalidOperationException("Claude: " + (string?)node?["result"]);
        }

        return (string?)node?["result"] ?? throw new InvalidOperationException("Claude: empty reply");
    }

    private static async Task<string> AnthropicApiAsync(string system, string prompt, CancellationToken ct)
    {
        var cfg = Config;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = Model(),
                max_tokens = 32000,
                system,
                messages = new[] { new { role = "user", content = prompt } },
            }),
        };
        req.Headers.Add("x-api-key", cfg.AiToken.Trim());
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Claude API: " + (string?)node?["error"]?["message"]);
        }

        AddUsage((long?)node?["usage"]?["input_tokens"] ?? 0, (long?)node?["usage"]?["output_tokens"] ?? 0);
        return string.Concat((node?["content"]?.AsArray() ?? new JsonArray()).Select(c => (string?)c?["text"]));
    }

    private static async Task<string> OpenAiApiAsync(string system, string prompt, CancellationToken ct)
    {
        var cfg = Config;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent.Create(OpenAiBody(Model(), system, prompt)),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.AiToken.Trim());
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("OpenAI API: " + (string?)node?["error"]?["message"]);
        }

        // reasoning tokens are billed as output and are included in completion_tokens
        AddUsage((long?)node?["usage"]?["prompt_tokens"] ?? 0, (long?)node?["usage"]?["completion_tokens"] ?? 0);
        return (string?)node?["choices"]?[0]?["message"]?["content"] ?? string.Empty;
    }

    /// <summary>
    /// Local server. Ollama's own API first (it lets us raise the context window and switch off "thinking");
    /// a URL ending in /v1, or a server without /api/chat, gets the OpenAI-compatible chat API.
    /// </summary>
    private static async Task<string> LocalAsync(string system, string prompt, CancellationToken ct)
    {
        var baseUrl = LocalBase(out var openAiStyle);
        var model = Model();
        if (string.IsNullOrEmpty(model))
        {
            throw new InvalidOperationException("Lokální AI: vyber model (tlačítko Načíst modely).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30)); // a big local model on a CPU is slow
        if (!openAiStyle)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/chat")
            {
                Content = JsonContent.Create(new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["stream"] = false,
                    ["think"] = false,
                    ["messages"] = new object[] { new { role = "system", content = system }, new { role = "user", content = prompt } },
                    ["options"] = new { num_ctx = 16384, temperature = 0.3 },
                }),
            };
            using var resp = await LocalHttp.SendAsync(req, timeout.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (resp.StatusCode != System.Net.HttpStatusCode.NotFound || body.Contains("model", StringComparison.OrdinalIgnoreCase))
            {
                var node = JsonNode.Parse(body);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Lokální AI: " + ((string?)node?["error"] ?? resp.StatusCode.ToString()));
                }

                return StripThinking((string?)node?["message"]?["content"] ?? string.Empty);
            }
        }

        using var req2 = new HttpRequestMessage(HttpMethod.Post, (openAiStyle ? baseUrl : baseUrl + "/v1") + "/chat/completions")
        {
            Content = JsonContent.Create(new Dictionary<string, object>
            {
                ["model"] = model,
                ["temperature"] = 0.3,
                ["messages"] = new object[] { new { role = "system", content = system }, new { role = "user", content = prompt } },
            }),
        };
        if (!string.IsNullOrWhiteSpace(Config.AiLocalKey))
        {
            req2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Config.AiLocalKey.Trim());
        }

        using var resp2 = await LocalHttp.SendAsync(req2, timeout.Token).ConfigureAwait(false);
        var n2 = JsonNode.Parse(await resp2.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        if (!resp2.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Lokální AI: " + ((string?)n2?["error"]?["message"] ?? (string?)n2?["error"] ?? resp2.StatusCode.ToString()));
        }

        return StripThinking((string?)n2?["choices"]?[0]?["message"]?["content"] ?? string.Empty);
    }

    /// <summary>Models offered by the local server (Ollama /api/tags, else OpenAI /v1/models).</summary>
    public static async Task<List<string>> LocalModelsAsync(CancellationToken ct)
    {
        var baseUrl = LocalBase(out var openAiStyle);
        if (!openAiStyle)
        {
            using var resp = await LocalHttp.GetAsync(baseUrl + "/api/tags", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return (node?["models"]?.AsArray() ?? new JsonArray()).Select(m => (string?)m?["name"]).OfType<string>().ToList();
            }
        }

        using var resp2 = await LocalHttp.GetAsync((openAiStyle ? baseUrl : baseUrl + "/v1") + "/models", ct).ConfigureAwait(false);
        resp2.EnsureSuccessStatusCode();
        var n2 = JsonNode.Parse(await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return (n2?["data"]?.AsArray() ?? new JsonArray()).Select(m => (string?)m?["id"]).OfType<string>().ToList();
    }

    private static readonly HttpClient LocalHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string LocalBase(out bool openAiStyle)
    {
        var url = Config.AiLocalUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Lokální AI: zadej adresu serveru, např. http://192.168.1.50:11434");
        }

        openAiStyle = u.AbsolutePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase);
        return url;
    }

    /// <summary>Thinking models (Qwen3, DeepSeek-R1…) may put their reasoning before the answer.</summary>
    private static string StripThinking(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"<think>.*?</think>", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

    /// <summary>Reasoning models (gpt-5*, o*) think before answering – translation needs little of that.</summary>
    private static Dictionary<string, object> OpenAiBody(string model, string system, string prompt)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[] { new { role = "system", content = system }, new { role = "user", content = prompt } },
            ["max_completion_tokens"] = 64000,
        };
        if (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || model.StartsWith('o'))
        {
            body["reasoning_effort"] = "low";
        }

        return body;
    }

    private async Task<string> RunAsync(string exe, List<string> args, string stdin, Dictionary<string, string> env, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("czat-ai-");
        try
        {
            return await RunInAsync(exe, args, stdin, env, work.FullName, ct).ConfigureAwait(false);
        }
        finally
        {
            work.Delete(true);
        }
    }

    private async Task<string> RunInAsync(string exe, List<string> args, string stdin, Dictionary<string, string> env, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(exe + " did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var outTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var errTask = p.StandardError.ReadToEndAsync(timeout.Token);
        await p.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token).ConfigureAwait(false);
        p.StandardInput.Close();
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            throw;
        }

        var stdout = await outTask.ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var err = (await errTask.ConfigureAwait(false)).Trim();
            _logger.LogWarning("{Exe} exit {Code}: {Err}", exe, p.ExitCode, err.Length > 500 ? err[..500] : err);
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {(stdout.Length > 0 ? stdout[..Math.Min(300, stdout.Length)] : err[..Math.Min(300, err.Length)])}");
        }

        return stdout;
    }
}
