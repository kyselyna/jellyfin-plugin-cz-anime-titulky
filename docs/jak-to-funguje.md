# Jak to funguje

```
Jellyfin (nový díl / denní úloha / ruční hledání)
   │  SubtitleSearchRequest (jazyk cze, cesta k videu)
   ▼
CzAnimeSubtitleProvider.Search
   ├─ přeskočit? (jiný jazyk, vynechaný seriál, už má CZ/SK titulky)
   ├─ HiyoriResolver: díl → AniList id sezóny → stránka hiyori.cz
   ├─ Rank: řádky tabulky hiyori pro tento díl, seřazené podle kvality
   └─ výsledky (automaticky: 1 výsledek s až 3 náhradami)
   ▼
CzAnimeSubtitleProvider.GetSubtitles
   ├─ stažení z webu (hiyori / hns / kamui / wosir / ange / HannyaSubs / přímý odkaz)
   ├─ PayloadExtractor: rozbalení archivu, výběr souboru, dekódování
   ├─ LanguageDetector: čeština nebo slovenština podle textu
   ├─ SyncService: srovnání časování (anglické titulky ve videu → zvuk)
   └─ SubtitleResponse → Jellyfin uloží video.cs.default.ass
```

## Párování dílu se stránkou na hiyori.cz

Jellyfin číslo sezóny a AniList číslování nesouhlasí (AniList má každou sezónu jako samostatné anime).
Proto:

1. **AniList id seriálu** – z metadat seriálu (doplněk *AniList* ho doplňuje; jinak hledání podle názvu).
   Pokud má vlastní AniList id **sezóna**, použije se přímo.
2. **Řetězec pokračování** – z AniList se postupně čte vztah `SEQUEL` (jen formáty TV, TV_SHORT, ONA).
   Sezóna 1 = první článek, sezóna 2 = druhý… (filmy a OVA se přeskakují).
3. **Stránka na hiyori.cz** – hledá se podle japonského, anglického názvu a synonym
   (`POST /rozcestnik`), u každého kandidáta se porovná odkaz `anilist.co/anime/<id>`.
   Nalezené párování se pamatuje natrvalo, „nenalezeno“ týden.
4. **Číslo dílu** – číslo dílu v sezóně. Některé stránky na hiyori pokračují v číslování předchozí sezóny
   (2. sezóna = díly 13–24); to se pozná a přepočítá.

Speciály (sezóna 0) se nepárují. Filmy: AniList id filmu nebo hledání podle názvu, díl = 1.

## Výběr titulků

Z tabulky na stránce hiyori (`#subs-panel`) se vezmou řádky s jazykem CZ/SK a podporovaným odkazem.
Pořadí (od nejdůležitějšího):

1. přesně tento díl (před balíkem „1–12“),
2. požadovaný jazyk (čeština; slovenština jen když česká verze k dílu není),
3. podporovaný web před obecným odkazem,
4. titulky pro stejný release jako video – skupina z názvu souboru (`[SubsPlease] …`) je ve sloupci *Release*,
5. BD vs. TV podle názvu souboru (`BD`, `Bluray`, `BDRip`),
6. skupina, která má titulky k nejvíc dílům sezóny (stejný styl a překlad v celé sezóně),
7. vyšší verze titulků.

**Automatický režim** – Jellyfin si vezme jen první výsledek. Proto doplněk vrátí jeden výsledek,
který v sobě nese až tři nejlepší verze; když první nejde stáhnout nebo prokazatelně nesedí časování,
zkusí se další.

**Id výsledku** je zakódovaná (base64) informace o videu a verzích – doplněk si proto nic nemusí pamatovat
mezi hledáním a stažením.

## Weby

| Web | Jak se stahuje |
|---|---|
| hiyori.cz | odkaz `/anime/downloadsubtitles?id=N` vrací přímo soubor |
| hns.sk | stránka anime → stránky dílů (číslo dílu z textu „ - N Zpět na anime“) → formulář `action=download` (POST) |
| kamui-subs.cz | stránka projektu → odkaz „N. Díl“ → `/download/<id>/` → archiv chráněný heslem (heslo z nastavení doplňku) |
| wosir.cz | odkaz z hiyori přesměruje na `/anime?id=N` → tabulka `div.subs tr.tsubs` → formulář TV nebo BD (podle videa) |
| ange.3mka.cz | stránka projektu `/anime/preklady/<název>` → odkazy jDownloads po dílech v pořadí dílů (bez čísla, jen název epizody) → číslo dílu se ověří podle názvu staženého souboru; balík všech dílů jako záloha. Bez přihlášení. |
| hannya-subs.blogspot.com | příspěvek projektu → odkazy `mega.nz/file/…` po dílech → podle zašifrovaného názvu souboru (MEGA atributy) se ověří číslo dílu, pak stažení a rozšifrování (AES-128-CTR klíčem z odkazu). Bez přihlášení. |
| teamns.gensubs.cz | archiv skončené skupiny TeamNS – hiyori odkazuje jen na úvodní stránku. Seznam archivů (`hotovo.php`, `nedodelano.php`) → ZIP sezóny, jehož název odpovídá názvu sezóny na AniList (podobnost ≥ 90 %, stejná čísla a symboly jako „∬“) → díl podle čísla v názvu souboru |
| přímé odkazy | odkazy končící `.ass/.srt/.ssa/.zip/.rar` (jen veřejné adresy – do místní sítě se doplněk nepřipojí nikdy) |

Nepodporované (odkaz se přeskočí): mega.nz (hannya-subs), hanabi.fan, web.archive.org stránky, neoexorcist a další weby bez přímého souboru.

Každý web má vlastní relaci (cookies), přihlásí se při prvním použití a znovu, když server místo souboru vrátí HTML.
Mezi požadavky na jeden web je prodleva (výchozí 700 ms).

## Rozbalení a jazyk

- zip a rar (knihovna SharpCompress, kterou má Jellyfin), heslo z nastavení, až 2 úrovně vnořených archivů;
- z více souborů se vybere ten s číslem dílu v názvu (`S01E05`, ` - 05 `, `_05.`), jinak největší;
- kódování: UTF-8, UTF-16 (BOM), jinak Windows-1250 (starší české titulky); výstup vždy UTF-8;
- **jazyk** podle znaků: `ř ů ě` = čeština, `ä ô ľ ĺ ŕ` = slovenština (štítek na hiyori občas nesedí).

## Srovnání časování

Pro srovnání se používají jen **dialogy** – řádky ve stylech pro nápisy, písně, karaoke ap. (`sign`, `song`, `op`, `ed`,
`kara`, `title`, `note`, `screen`, `caption`) se ignorují, ale posouvají se spolu s ostatními.

### 1. Podle titulků ve videu

Jellyfinův ffmpeg z videa vytáhne textové titulkové stopy – přednostně anglické (nebo bez jazyka); když nejsou nebo jsou
moc krátké, i v jiném jazyce (francouzské, německé…). Porovnávají se jen **začátky vět**, ne text, takže jazyk vzoru nevadí.
Vybere se stopa s nejvíc dialogy („Full Subtitles“, ne „Signs & Songs“), potřeba je aspoň 50 řádků. České/slovenské stopy se nepoužijí.

1. **Rychlost** – zkusí se poměry běžných snímkových frekvencí (23,976 / 24 / 25 fps); u jiné frekvence titulky
   rovnoměrně „ujíždějí“ (např. o 1 s za 20 minut).
2. **Globální posun** – hledá se posun v rozsahu ± *Největší povolený posun*, nejdřív po 0,1 s, pak zpřesnění po 0,02 s.
   Skóre = podíl českých řádků, jejichž začátek je do 0,25 s od začátku anglického řádku.
3. **Kontrola po částech** – titulky se rozdělí na 5 částí a pro každou se hledá vlastní posun.
   **Ověřeno**, když sedí ≥ 60 % řádků a posuny částí se liší nejvýš o 0,5 s.
4. **Jiný střih** – vždy (i když globální posun sedí) se hledají místa, kde video a titulky stříhají jinak:
   z oken po 12 řádcích se vezmou kandidátní posuny a každému řádku se přiřadí jeden tak, aby bylo co nejméně
   nesedících řádků + 4 body za každou změnu posunu (Viterbi). Použije se, když po posunu sedí ≥ 60 % řádků
   a je to o ≥ 3 procentní body lepší než jeden posun.
   (Příklady: Coppelion BD vs. titulky pro TV – původně 12 % shody, po částech 90–98 %; Ao no Exorcist BD – úvodní scéna +9,9 s, zbytek −0,1 s.)
5. Jinak **nesedí** – v automatickém režimu se titulky neuloží a zkusí se další verze.

### 2. Podle zvuku

Když video anglické titulky nemá:

1. ffmpeg dekóduje první zvukovou stopu na 8 kHz mono s pásmovou propustí 200–3000 Hz (řečové pásmo);
2. hlasitost v 10ms úsecích (log energie);
3. **detekce řeči** – úsek je „řeč“, když je o 0,35 (log10) hlasitější než místní pozadí
   (10. percentil okolních 3 s). Tím se odfiltruje hudba v pozadí, která dělá globální práh nepoužitelným;
4. „titulek na obrazovce“ ze začátků a konců dialogů;
5. **křížová korelace** obou signálů (±1) přes FFT, pro všechny poměry rychlosti; vybere se nejvyšší vrchol;
6. **jistota** = z-skóre vrcholu; přijme se, když je ≥ 3,0 a o ≥ 0,7 vyšší než nejlepší jiný posun vzdálený víc než 1 s;
7. posun se opraví o `start_time` zvukové stopy (některé MKV mají zvuk posunutý o desetiny až jednotky sekund);
8. **po částech** – dialogy se rozdělí na ~10 částí a každá se srovná se zvukem v okně ±20 s kolem celkového posunu.
   Část se bere vážně, když je sama jistá (z ≥ 3), nebo středně jistá (z ≥ 2,3) a soused se s ní shoduje do 1,5 s.
   Když se jisté části liší od celkového posunu o víc než 0,6 s, posouvá se po částech.

Na testovací sadě 22 dílů se známým správným posunem (z anglických titulků nebo z ffsubsync) byla zvuková metoda
21× jistá s průměrnou odchylkou ~0,2 s, jednou nejistá (titulky se pak neposunou) a ani jednou jistě špatně.

### 3. Bez ověření

Když není s čím srovnat, titulky se uloží beze změny (volba *Automaticky uložit i titulky s neověřeným časováním*).

## Uložení

Doplněk vrátí Jellyfinu jazyk `cs.default` (nebo `sk.default`) a Jellyfin soubor uloží jako
`<název videa>.cs.default.ass` vedle videa. Jellyfin z názvu pozná jazyk (Czech) i příznak výchozí stopy.

## Mezipaměť a historie

`plugins/Jellyfin.Plugin.CzAnimeTitulky/` v datovém adresáři Jellyfinu:

| Soubor | Obsah | Platnost |
|---|---|---|
| `cache/anilist-media.json` | AniList záznamy (názvy, pokračování) | 7 dní |
| `cache/anilist-search.json` | hledání podle názvu | 30 dní |
| `cache/hiyori-map.json` | AniList id → stránka hiyori | trvale (nenalezeno 7 dní) |
| `activity.log` | historie (max 2 MB + 1 starší soubor) | |

Tabulky titulků z hiyori a extrahované anglické titulky / zvuk se drží jen v paměti (12 h, resp. 30 min).
