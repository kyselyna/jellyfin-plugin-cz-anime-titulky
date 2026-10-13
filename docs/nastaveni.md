# Nastavení

## 1. Doplněk

*Ovládací panel → Doplňky → CZ Anime Titulky*

### Přihlášení k webům

| Pole | Poznámka |
|---|---|
| hiyori.cz – uživatel / heslo | **nutné** – bez přihlášení hiyori neukazuje tabulku titulků, doplněk pak nic nenajde |
| hns.sk – e-mail / heslo | přihlašuje se e-mailem |
| kamui-subs.cz – uživatel / heslo | |
| wosir.cz – přezdívka / heslo | |
| kamui-subs.cz – heslo k archivům | kamui-subs.cz dává titulky do archivů chráněných heslem – heslo najdeš na jejich webu. Bez něj se titulky z kamui nerozbalí. |

Tlačítko **Vyzkoušet přihlášení** se přihlásí na všechny weby a ukáže `OK` / `CHYBA přihlášení` / `nevyplněno`.
Nastavení nejdřív ulož.

Hesla ukládá Jellyfin do `plugins/configurations/Jellyfin.Plugin.CzAnimeTitulky.xml` na serveru
(čitelné jen pro správce serveru). Do repozitáře ani nikam jinam se neposílají.

### Jazyk a pojmenování

| Volba | Výchozí | Význam |
|---|---|---|
| Když nejsou české, vzít slovenské | zapnuto | Při hledání češtiny nabídne slovenské titulky, pokud k dílu žádné české nejsou. |
| Ukládat jako výchozí stopu | zapnuto | Soubor se jmenuje `video.cs.default.ass` – přehrávače titulky zapnou samy. Vypnuto: `video.cs.ass`. |

### Časování

| Volba | Výchozí | Význam |
|---|---|---|
| Srovnat podle anglických titulků ve videu | zapnuto | Nejpřesnější metoda, viz [jak-to-funguje.md](jak-to-funguje.md#1-podle-anglických-titulků-ve-videu). |
| Když video titulky nemá, srovnat podle zvuku | zapnuto | |
| Největší povolený posun (s) | 90 | BD a TV verze se liší i o 10 s, některé releasy o desítky sekund. |
| Automaticky uložit i titulky s neověřeným časováním | zapnuto | Když není s čím srovnat (žádná anglická stopa a zvuk není jednoznačný), titulky se uloží beze změny. Titulky, které na anglickou stopu **prokazatelně nesedí**, se automaticky neuloží nikdy – zkusí se další verze. |

### Vlastní sbírka titulků

| Volba | Výchozí | Význam |
|---|---|---|
| Složky s vlastními titulky | prázdné | Jedna cesta na řádek, **tak jak ji vidí server Jellyfinu** (např. `/media/titulky`, v Dockeru cesta uvnitř kontejneru). |
| Dávat přednost vlastní sbírce před weby | vypnuto | Vypnuto: sbírka se použije, když na webech nic není (nebo jako náhrada, když webové titulky nesedí). |

Co doplněk ve složkách najde:

- **podsložku s titulky** – název podsložky = název anime/sezóny (`Haikyuu!!/…ass`);
- **archiv zip/rar** – název archivu = název anime/sezóny (`Code Geass_ Hangyaku no Lelouch R2.zip`,
  `Mushoku Tensei - všechny titulky.zip`); heslo z pole *kamui-subs.cz – heslo k archivům*;
- **sbírkový archiv** s víc sériemi uvnitř (např. `teamns-archiv.zip` → `hotovo/<série>/…`) – každá vnitřní složka je samostatná položka.

Názvy se porovnávají s názvy sezóny na AniList (japonský, anglický, synonyma) tolerantně – stejná čísla a symboly
(„2“, „R2“, „∬“) se musí shodovat. Doplňky jako „ - všechny titulky“, „komplet“, `[…]`, `(…)` se ignorují.
Díl se vybere podle čísla v názvu souboru; když soubor nese `SxxEyy`, musí sedět i sezóna.

Tlačítko **Prohledat sbírku** (po uložení) ukáže, kolik sérií/sezón doplněk našel a jak se jmenují –
když něco chybí, přejmenuj složku/archiv podle anglického nebo japonského názvu na AniList.

Doplněk ze složek **jen čte**.

### Ostatní

| Volba | Výchozí | Význam |
|---|---|---|
| Vynechat seriály (regulární výraz) | prázdné | Seriály s českým dabingem nebo titulky v obraze, např. `^(Naruto|Fairy Tail|Digimon|Pok[eé]mon)` |
| Přeskočit díly, které už mají CZ/SK titulky vedle videa | zapnuto | Automatické hledání vynechá díl, u kterého už leží `*.cs.*` / `*.sk.*` soubor titulků. |
| Mezipaměť seznamů titulků | 24 h | Jak dlouho se pamatuje tabulka titulků z hiyori. |
| Prodleva mezi požadavky | 700 ms | Ohleduplnost k webům – požadavky na jeden web jdou vždy jeden po druhém. |
| Nejvíc požadavků na jeden web za den | 500 | Pojistka: po vyčerpání se web do půlnoci přeskočí (v historii `LIMIT`). |
| Titulky, které k dílu nesedí, zkusit znovu za | 14 dní | Automatické hledání je mezitím přeskočí – žádné opakované stahování každý den. |

### Historie

Tlačítko **Zobrazit posledních 300 záznamů** ukáže, co doplněk dělal:

```
2026-10-12 16:42:39  HLEDÁNÍ  Coppelion S01E02 (CZ, automaticky): 1× – nejlepší Hns.sk [HorribleSubs] SK
2026-10-12 16:43:47  ULOŽENO  Coppelion S01E02: SK Hns.sk [HorribleSubs] z hns, časování: reference po částech, 98 %
```

Záznamy: `HLEDÁNÍ`, `ULOŽENO`, `NEULOŽENO` (s důvodem pro každou zkoušenou verzi), `NENALEZENO`, `PŘESKOČENO` (mimo jiné dříve zamítnuté verze), `CHYBA` (např. Jellyfin nedokázal soubor zapsat).
Soubor leží v `plugins/Jellyfin.Plugin.CzAnimeTitulky/activity.log` v datovém adresáři Jellyfinu (mezipaměť vedle ve složce `cache/`).

## 2. Knihovna anime

*Ovládací panel → Knihovny → (anime) → … → Spravovat knihovnu*, sekce **Titulky**:

| Volba | Nastavit |
|---|---|
| Jazyky stahování titulků | **Czech** (jen češtinu – slovenské doplněk nabídne sám, když české nejsou; při Czech + Slovak by se stahovalo obojí) |
| Stahovače titulků | **CZ Anime Titulky** zaškrtnout a dát na první místo; OpenSubtitles bez účtu raději vypnout |
| Vyžadovat přesnou shodu | může zůstat zapnuto (doplněk párování ověřuje sám) |
| Přeskočit, pokud video už obsahuje vložené titulky | **vypnout** – anime mívá vložené anglické titulky |
| Přeskočit, pokud výchozí zvuková stopa odpovídá jazyku | zapnout – díly s českým dabingem se přeskočí |
| Ukládat titulky do složek s médii | **zapnout** |

Doplněk funguje i pro filmy (anime filmy), stačí nastavit totéž v knihovně filmů – v knihovnách,
které nejsou anime, ale nemá smysl (hledá jen na anime webech).

## 3. Kdy se titulky stahují

- **Nový díl / nová série** – při skenování knihovny Jellyfin nové položky prozkoumá a zavolá doplněk.
  U právě vysílaných anime titulky často ještě nejsou – doplněk to zkusí znovu při denní úloze.
- **Naplánované úlohy → Stáhnout chybějící titulky** – projde celou knihovnu (výchozí 1× denně).
  Díly, které už české/slovenské titulky mají, přeskočí bez dotazu na weby.
- **Ručně** – detail dílu → *⋯ → Upravit titulky → Hledat* (jazyk Czech) → vybrat verzi.
  Ruční volba se uloží vždy (i když časování nejde ověřit) a historie řekne, jak dopadlo.
- **Jeden díl znovu** – smaž soubor titulků a dej *⋯ → Obnovit metadata → Hledat chybějící metadata*
  nebo *Nahradit všechna metadata*.

## 4. Uživatelé

S volbou *Ukládat jako výchozí stopu* není potřeba nic. Bez ní nastav každému uživateli
*Profil → Přehrávání → Jazyk titulků: Czech* a *Režim titulků: Výchozí*.
