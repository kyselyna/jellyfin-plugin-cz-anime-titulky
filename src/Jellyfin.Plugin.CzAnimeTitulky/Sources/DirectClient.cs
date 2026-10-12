using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Sources;

/// <summary>Plain HTTP download for links that point straight at a subtitle file or archive.</summary>
public sealed class DirectClient : SiteClient
{
    public DirectClient(ILogger<DirectClient> logger)
        : base(logger)
    {
    }

    public override string Name => "direct";

    protected override (string User, string Password) Credentials => ("-", "-");

    protected override Task<bool> LoginAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<Payload?> DownloadAsync(string url, CancellationToken ct) => DownloadGetAsync(url, ct);
}
