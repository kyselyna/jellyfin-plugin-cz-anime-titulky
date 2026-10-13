# Vývoj, sestavení a vydání

## Struktura

```
src/Jellyfin.Plugin.CzAnimeTitulky/
  Plugin.cs                    vstupní bod doplňku (název, GUID, stránka nastavení)
  PluginServiceRegistrator.cs  registrace služeb (ISubtitleProvider + klienti webů)
  Configuration/               nastavení + configPage.html
  Providers/                   CzAnimeSubtitleProvider (Search / GetSubtitles), Ticket (id výsledku)
  Matching/                    AniList, párování na hiyori.cz
  Sources/                     klienti webů (hiyori, hns, kamui, wosir, ange, HannyaSubs/mega.nz, přímé odkazy)
  Subtitles/                   načtení/posun ASS a SRT, rozbalení archivů
  Sync/                        srovnání časování (reference, zvuk), ffmpeg
  Api/                         endpointy pro stránku nastavení
  Util/                        mezipaměť, detekce jazyka, historie
tools/AlignTest/               offline test srovnávacích algoritmů na dílech se známým posunem
```

## Sestavení

Potřeba: .NET SDK 10.

```bash
dotnet build src/Jellyfin.Plugin.CzAnimeTitulky -c Release
# výstup: src/Jellyfin.Plugin.CzAnimeTitulky/bin/Release/net10.0/
#   Jellyfin.Plugin.CzAnimeTitulky.dll + AngleSharp.dll  → do složky doplňku
```

Knihovny Jellyfinu a SharpCompress se jen referencují (`ExcludeAssets="runtime"`) – na serveru je dodá Jellyfin.

## Test srovnání časování

`tools/AlignTest` přijme JSON se seznamem případů (`sub`, `ref`, `off`, `astart`) a složku se zvukem
(`<n>.pcm` = 8 kHz mono s16le, pásmová propust):

```bash
ffmpeg -i video.mkv -vn -map 0:a:0 -ac 1 -ar 8000 -af highpass=f=200,lowpass=f=3000 -f s16le 1.pcm
dotnet run -c Release --project tools/AlignTest -- cases.json pcm/
```

## Vydání nové verze

1. Zvyš verzi v `build.yaml` a `<AssemblyVersion>/<FileVersion>` v `.csproj`, doplň `CHANGELOG.md`.
2. `git tag v1.1.0 && git push --tags`
3. GitHub Actions (`.github/workflows/release.yml`) sestaví ZIP, vytvoří Release a zaktualizuje `manifest.json`
   (katalog doplňků pro Jellyfin).

## Po aktualizaci Jellyfinu

Jellyfin načítá jen doplňky sestavené pro kompatibilní verzi (`targetAbi`). Při velké aktualizaci
(nová hlavní verze, nový .NET):

1. v `.csproj` zvyš verze balíčků `Jellyfin.Controller` / `Jellyfin.Model` na novou verzi serveru
   a `SharpCompress` na verzi, kterou Jellyfin přibaluje (`grep SharpCompress /usr/lib/jellyfin/bin/jellyfin.deps.json`),
   případně `TargetFramework` (viz `/usr/lib/jellyfin/bin/jellyfin.runtimeconfig.json`);
2. v `build.yaml` uprav `targetAbi` a `framework`;
3. sestav, oprav případné změny API, vydej novou verzi.

Rozhraní, která doplněk používá: `ISubtitleProvider`, `IPluginServiceRegistrator`, `BasePlugin<T>`, `IHasWebPages`,
`ILibraryManager.FindByPath/GetItemById`, `IMediaEncoder.EncoderPath/ProbePath`.
