using Jellyfin.Plugin.CzAnimeTitulky.Sources;
using Jellyfin.Plugin.CzAnimeTitulky.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CzAnimeTitulky.Matching;

/// <summary>What we are looking subtitles for.</summary>
public sealed record Target(Video Video, string DisplayName, int Episode, HiyoriAnime Anime, int AniListId);

/// <summary>
/// Maps a Jellyfin episode/movie to the hiyori.cz page of the right season:
/// series AniList id → AniList TV sequel chain (season N = N-th entry) → hiyori page whose
/// AniList link equals that id.
/// </summary>
public sealed class HiyoriResolver
{
    private readonly AniListClient _aniList;
    private readonly HiyoriClient _hiyori;
    private readonly ILogger<HiyoriResolver> _logger;
    private readonly JsonCache<string> _map = new("hiyori-map");

    public HiyoriResolver(AniListClient aniList, HiyoriClient hiyori, ILogger<HiyoriResolver> logger)
    {
        _aniList = aniList;
        _hiyori = hiyori;
        _logger = logger;
    }

    public async Task<Target?> ResolveAsync(Video video, CancellationToken ct)
    {
        var ids = await ResolveIdsAsync(video, ct).ConfigureAwait(false);
        if (ids is null)
        {
            return null;
        }

        var anime = await FindHiyoriAsync(ids.Value.AniListId, ct).ConfigureAwait(false);
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
            aniId = await SeasonAniListIdAsync(ep, season, ct).ConfigureAwait(false);
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

    private async Task<int?> SeasonAniListIdAsync(Episode ep, int season, CancellationToken ct)
    {
        // A season may carry its own AniList id (AniList plugin / manual identify).
        var own = ParseId(ep.Season?.GetProviderId("AniList"));
        if (own is not null)
        {
            return own;
        }

        var series = ep.Series;
        var first = ParseId(series?.GetProviderId("AniList"));
        if (first is null && series is not null)
        {
            first = await _aniList.SearchAsync(series.Name, ct).ConfigureAwait(false);
        }

        if (first is null)
        {
            return null;
        }

        return (await _aniList.GetSeasonAsync(first.Value, season, ct).ConfigureAwait(false))?.Id;
    }

    private async Task<HiyoriAnime?> FindHiyoriAsync(int aniId, CancellationToken ct)
    {
        var key = aniId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_map.TryGet(key, TimeSpan.FromDays(3650), out var path) && path is not null)
        {
            return await _hiyori.GetAnimeAsync(path, ct).ConfigureAwait(false);
        }

        if (_map.TryGet(key, TimeSpan.FromDays(7), out _))
        {
            return null; // recently not found
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
                if (page.AniListId == aniId)
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
