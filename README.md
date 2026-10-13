# CZ Anime Titulky – doplněk pro Jellyfin

Doplněk do Jellyfinu, který k anime sám najde **české titulky** (a když české nejsou, tak **slovenské**),
stáhne je z českých a slovenských fansub webů, **srovná jejich časování s videem** a uloží je tak,
aby se při přehrávání zapnuly samy.

Chová se jako běžný poskytovatel titulků (stejně jako OpenSubtitles):

- **automaticky** – když přibude nový díl nebo celá série a při denní úloze *Stáhnout chybějící titulky*,
- **ručně** – v přehrávači nebo v detailu dílu přes *Upravit titulky → Hledat* si vybereš konkrétní verzi.

## Co umí

| | |
|---|---|
| **Zdroje** | [hiyori.cz](https://hiyori.cz) (rozcestník + titulky hostované přímo na hiyori), [hns.sk](https://hns.sk), [kamui-subs.cz](https://kamui-subs.cz), [wosir.cz](https://www.wosir.cz), [ange.3mka.cz](https://ange.3mka.cz), [HannyaSubs](https://hannya-subs.blogspot.com) (soubory na mega.nz), archiv [TeamNS](https://teamns.gensubs.cz) a přímé odkazy na soubory |
| **Vlastní sbírka** | složky a archivy s tvými titulky (např. na NAS) jako další zdroj |
| **Párování** | seriál → AniList id → řetězec pokračování na AniList (sezóna N = N-tý díl řetězce) → stránka na hiyori.cz se stejným AniList id |
| **Výběr** | čeština před slovenštinou, skupina, která má titulky k co nejvíc dílům sezóny, titulky pro stejný release (skupina v názvu souboru, BD vs. TV), nejvyšší verze |
| **Archivy** | zip / rar i chráněné heslem (heslo k archivům kamui-subs.cz zadáš v nastavení), vnořené archivy, z balíku celé série vybere správný díl |
| **Jazyk** | pozná češtinu a slovenštinu podle textu (štítky na webech občas nesedí), vstup v UTF-8, UTF-16 i Windows-1250 |
| **Časování** | 1) podle anglických titulků ve videu (globální posun, kontrola po částech, jiný střih po částech, jiná snímková frekvence), 2) když video titulky nemá, podle zvuku (detekce řeči + korelace) |
| **Uložení** | `video.cs.default.ass` / `video.sk.default.ass` vedle videa – přehrávače je zapnou samy |
| **AI překlad** | volitelně: když fansub nikde není, přeloží titulky z videa do češtiny – Claude (předplatné claude.ai nebo API) nebo OpenAI (API, doporučeno gpt-5-mini ≈ 0,02 $ za díl) – [docs/ai-preklad.md](docs/ai-preklad.md) |
| **Historie** | stránka nastavení ukazuje, co se kdy hledalo, uložilo a proč něco uloženo nebylo |

## Rychlý start

1. Nainstaluj doplněk – viz [docs/instalace.md](docs/instalace.md).
2. *Ovládací panel → Doplňky → CZ Anime Titulky*: vyplň přihlašovací údaje k webům a klikni na **Vyzkoušet přihlášení**.
3. *Ovládací panel → Knihovny → Anime → Spravovat knihovnu → Titulky*:
   - stahovat jazyky: **Czech**,
   - pořadí stahovačů: **CZ Anime Titulky** první,
   - *Přeskočit, pokud video už obsahuje vložené titulky* **vypnout** (anime mívá vložené anglické),
   - *Ukládat titulky do složek s médii* **zapnout**.
4. Hotovo. Nové díly dostanou titulky samy, na stávající si můžeš spustit *Naplánované úlohy → Stáhnout chybějící titulky*.

Podrobně: [docs/nastaveni.md](docs/nastaveni.md).

## Dokumentace

- [Instalace](docs/instalace.md)
- [Nastavení](docs/nastaveni.md)
- [Jak to funguje](docs/jak-to-funguje.md) – párování, výběr, stahování, srovnání časování
- [AI překlad](docs/ai-preklad.md)
- [Řešení problémů](docs/reseni-problemu.md)
- [Vývoj, sestavení a vydání nové verze](docs/vyvoj.md)

## Požadavky

- Jellyfin **12.1** nebo novější (.NET 10)
- účty na hiyori.cz, hns.sk, kamui-subs.cz a wosir.cz (zdarma; bez účtu se daný web přeskočí)

## Ohleduplnost k fansub webům

Fansub weby provozují dobrovolníci. Doplněk posílá na jeden web vždy jen jeden požadavek najednou s prodlevou 0,7 s,
má denní limit 500 požadavků na web, výsledky ukládá do mezipaměti (seznamy titulků 24 h, párování natrvalo),
nesedící titulky 14 dní nezkouší znovu a stahuje jen to, co opravdu chybí. Představuje se pravdivě (User-Agent obsahuje `Jellyfin-CzAnimeTitulky`).
Titulky jsou dílem překladatelů – nesdílej je dál bez jejich souhlasu.

## Licence

[GPL-3.0](LICENSE) (stejně jako Jellyfin).
