// Manual check of one source client: SourceTest <wayback|ange|hannya> <url> <episode>
using Jellyfin.Plugin.CzAnimeTitulky.Sources;
using Jellyfin.Plugin.CzAnimeTitulky.Subtitles;
using Microsoft.Extensions.Logging;

using var lf = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
var ep = int.Parse(args[2]);
Payload? p = args[0] switch
{
    "wayback" => await new WaybackClient(lf.CreateLogger<WaybackClient>()).DownloadAsync(args[1], ep, CancellationToken.None),
    "ange" => await new AngeClient(lf.CreateLogger<AngeClient>()).DownloadAsync(args[1], ep, CancellationToken.None),
    "hannya" => await new HannyaClient(lf.CreateLogger<HannyaClient>()).DownloadAsync(args[1], ep, CancellationToken.None),
    _ => null,
};
Console.WriteLine(p is null ? "NULL" : $"{p.FileName} {p.Content.Length} B, doc={(PayloadExtractor.Extract(p, ep, string.Empty) is { } d ? d.Format : "null")}");
