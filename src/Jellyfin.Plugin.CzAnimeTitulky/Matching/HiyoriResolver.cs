using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CzAnimeTitulky.Sources;
using Jellyfin.Plugin.CzAnimeTitulky.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Matching;

/// <summary>What we are looking subtitles for.</summary>
public sealed record Target(Video Video, string DisplayName, int Episode, HiyoriAnime Anime, int AniListId);

/// <summary>
/// Maps a Jellyfin episode/movie to the hiyori.cz page of the right season:
/// series AniList id → AniList TV sequel chain (season N = N-th entry) → hiyori page whose
/// AniList link equals that id. When the air date of the episode does not fit that entry (split seasons:
/// AniList "Part 2" entries count as extra links of the chain), the entry is picked by air date instead.
/// </summary>
public sealed class HiyoriResolver
{
    private readonly AniListClient _aniList;
    private readonly HiyoriClient _hiyori;
    private readonly ILibraryManager _library;
    private readonly ILogger<HiyoriResolver> _logger;
    private readonly JsonCache<string> _map = new("hiyori-map");

    public HiyoriResolver(AniListClient aniList, HiyoriClient hiyori, ILibraryManager library, ILogger<HiyoriResolver> logger)
    {
        _aniList = aniList;
        _hiyori = hiyori;
        _library = library;
        _logger = logger;
    }

    public async Task<Target?> ResolveAsync(Video video, bool fresh, CancellationToken ct)
    {
        var ids = await ResolveIdsAsync(video, ct).ConfigureAwait(false);
        if (ids is null)
        {
            return null;
        }

        var anime = await FindHiyoriAsync(ids.Value.AniListId, fresh, ct).ConfigureAwait(false);
        return anime is null ? null : new Target(video, ids.Value.Name, ids.Value.Episode, anime, ids.Value.AniListId);
    }

    /// <summary>AniList id of the season, episode number and a display name (without hiyori).</summary>
    public async Task<(int AniListId, int Season, int Episode, string Name)?> ResolveIdsAsync(Video video, CancellationToken ct)
    {
        int? aniId;
        var seasonNo = 1;
        int episode;
        string name;
        if (video is Episode ep)
        {
            if (ep.IndexNumber is null || (ep.ParentIndexNumber ?? 1) < 1)
            {
                return null; // specials (season 0) have no reliable mapping
            }

            var season = ep.ParentIndexNumber ?? 1;
            seasonNo = season;
            episode = ep.IndexNumber.Value;
            name = $"{ep.SeriesName} S{season:00}E{episode:00}";
            (aniId, episode) = await SeasonAniListIdAsync(ep, season, episode, ct).ConfigureAwait(false);
        }
        else if (video is Movie movie)
        {
            episode = 1;
            name = movie.Name;
            aniId = ParseId(movie.GetProviderId("AniList")) ?? await _aniList.SearchAsync(movie.Name, ct).ConfigureAwait(false);
        }
        else
        {
            return null;
        }

        if (aniId is null)
        {
            _logger.LogDebug("{Name}: no AniList id", name);
            return null;
        }

        return (aniId.Value, seasonNo, episode, name);
    }

    /// <summary>AniList id of the entry the episode belongs to and the episode number within that entry.</summary>
    private async Task<(int? Id, int Episode)> SeasonAniListIdAsync(Episode ep, int season, int episode, CancellationToken ct)
    {
        // A season may carry its own AniList id (AniList plugin / manual identify).
        var own = ParseId(ep.Season?.GetProviderId("AniList"));
        if (own is not null)
        {
            return (own, episode);
        }

        var series = ep.Series;
        var first = ParseId(series?.GetProviderId("AniList"));
        if (first is null && series is not null)
        {
            first = await _aniList.SearchAsync(series.Name, ct).ConfigureAwait(false);
        }

        if (first is null)
        {
            return (null, episode);
        }

        var counted = await _aniList.GetSeasonAsync(first.Value, season, ct).ConfigureAwait(false);
        // 1970-01-01 = missing date in the metadata, not an air date.
        if (ep.PremiereDate is not { Year: > 1970 } air || (counted is not null && (counted.Start is null || counted.Covers(air))))
        {
            return (counted?.Id, episode);
        }

        // N-th season of the chain aired at another time (e.g. Mushoku Tensei: S1 and S2 have a "Part 2" each,
        // so the 3rd chain entry is season 2) → take the entry that aired when the episode did.
        var byDate = (await _aniList.GetChainAsync(first.Value, ct).ConfigureAwait(false)).FirstOrDefault(m => m.Covers(air));
        if (byDate is null)
        {
            _logger.LogInformation("{Series} S{Season:00}E{Episode:00}: no AniList entry aired on {Air:yyyy-MM-dd}", ep.SeriesName, season, episode, air);
            return (null, episode); // better nothing than another season's subtitles
        }

        // "Part 2" inside one Jellyfin season: its episodes are numbered from 1 again.
        var before = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series!.Id },
            ParentIndexNumber = season,
            Recursive = true,
        }).Count(e => e.IndexNumber is > 0 && e.IndexNumber < episode && e.PremiereDate < byDate.Start!.Value.AddDays(-14));
        _logger.LogInformation("{Series} S{Season:00}E{Episode:00}: by air date AniList {Id} episode {Number}", ep.SeriesName, season, episode, byDate.Id, episode - before);
        return (byDate.Id, episode - before);
    }

    private async Task<HiyoriAnime?> FindHiyoriAsync(int aniId, bool fresh, CancellationToken ct)
    {
        var key = aniId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_map.TryGet(key, TimeSpan.FromDays(3650), out var path) && path is not null)
        {
            return await _hiyori.GetAnimeAsync(path, ct).ConfigureAwait(false);
        }

        // "Not found" is remembered for a day – except for a manual search, which always asks the site again.
        if (!fresh && _map.TryGet(key, TimeSpan.FromDays(1), out _))
        {
            return null;
        }

        var media = await _aniList.GetAsync(aniId, ct).ConfigureAwait(false);
        if (media is null)
        {
            return null;
        }

        var tried = new HashSet<string>();
        foreach (var title in media.Titles())
        {
            foreach (var cand in (await _hiyori.SearchAsync(title, ct).ConfigureAwait(false)).Take(10))
            {
                if (!tried.Add(cand))
                {
                    continue;
                }

                var page = await _hiyori.GetAnimeAsync(cand, ct).ConfigureAwait(false);
                // Some hiyori pages link only MyAnimeList (Overgeared) → the MAL id counts when there is no AniList link.
                if (page.AniListId == aniId || (page.AniListId is null && media.MalId is not null && page.MalId == media.MalId))
                {
                    _map.Set(key, cand);
                    _logger.LogInformation("AniList {Id} ({Title}) = hiyori {Path}", aniId, media.Romaji, cand);
                    return page;
                }
            }
        }

        _logger.LogInformation("AniList {Id} ({Title}) not found on hiyori.cz", aniId, media.Romaji);
        _map.Set(key, null);
        return null;
    }

    private static int? ParseId(string? s) => int.TryParse(s, out var i) ? i : null;
}
