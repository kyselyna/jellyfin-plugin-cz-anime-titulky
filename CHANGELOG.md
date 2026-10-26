# Změny

## 1.8.11 – 2026-10-26

- **Výběr sezóny podle počtu dílů.** Když má řada v Jellyfinu víc dílů, než má záznam na AniListu, patří zbytek
  do dalšího záznamu („Part 2“). Neplatí to o jednom dílu navíc, třeba rekapitulaci. Data vysílání z metadat
  se použijí jen tehdy, když sedí. DanMachi IV má v metadatech u většiny dílů datum 1. řady (2015), plugin je
  proto hledal u 1. řady. U DanMachi V dva takové díly posunuly číslování (S05E12 → „díl 10“). Sezóna se teď
  ani podle data nemůže přiřadit dřívějšímu záznamu, než jaký odpovídá jejímu číslu.
- **hiyori.cz: víc výsledků hledání a odhad podle AniDB.** U populárních titulů vrátí hledání spoustu sezón a
  filmů. DanMachi má 13 výsledků a 1. řada je poslední, plugin přitom bral jen prvních 10. Teď bere 20. Navíc
  nejdřív zkusí stránku s číslem AniDB id sezóny, protože hiyori je většinou používá.
- **hns.sk: přejmenované stránky anime.** hiyori u DanMachi II odkazuje na starou adresu, ze které hns.sk
  přesměruje na úvodní stránku. Když stránka anime nemá žádné díly, plugin anime najde v seznamu /animelist
  podle názvu z AniListu.

## 1.8.10 – 2026-10-26

- **Hledání na hiyori.cz po vypršení přihlášení.** Hiyori nepřihlášenému uživateli při hledání nic nevrátí a
  přihlášení po několika dnech běhu serveru vyprší. Plugin pak u nových anime hlásil „anime není na hiyori.cz“
  (Journal with Witch / Ikoku Nikki), i když tam titulky jsou. Když stránka s výsledky ukazuje odhlášeného
  uživatele, plugin se znovu přihlásí a hledá ještě jednou.

## 1.8.4 – 2026-10-19

- **Nový web HannyaSubs – [hannya-subs.cz](https://www.hannya-subs.cz).** HannyaSubs nově vydávají na vlastním webu
  (staré projekty zůstávají na blogspotu/mega.nz). Stahování vyžaduje účet – v nastavení přibyla pole
  „hannya-subs.cz – přezdívka / heslo“ (bez účtu se web přeskočí).
- **Titulky, které plugin neumí stáhnout.** Když jsou titulky k dílu jen na webu, odkud je stáhnout nejde (např.
  animetitulky.com – přihlášení chrání reCAPTCHA), historie napíše „RUČNĚ … stáhni je ručně a ulož vedle videa“
  místo „nemá titulky“. Při ručním hledání se takové titulky ukážou jako položka „⚠ Nelze stáhnout automaticky –
  stáhni ručně z …“ s odkazem; jejím výběrem se nic nestáhne, jen se vypíše, kde titulky jsou.

## 1.8.3 – 2026-10-19

- **Párování s hiyori i přes MyAnimeList.** Některé stránky hiyori.cz odkazují jen na MyAnimeList, ne na AniList
  (Overgeared / Tempal: Item no Chikara) – plugin je dřív nepoznal a hlásil „anime není na hiyori.cz“. Když
  stránka nemá odkaz na AniList, uzná se podle MAL id, které AniList u anime uvádí.
- Datum vysílání 1. 1. 1970 (chybějící údaj v metadatech) se bere jako neznámé – výběr sezóny podle data
  z 1.8.2 by u takových dílů nenabídl nic.

## 1.8.2 – 2026-10-18

- **Správná sezóna u rozdělených sérií.** AniList vede „Part 2“ jako samostatnou položku, takže N-tá položka
  řetězce nemusí být N-tá sezóna (Mushoku Tensei: 3. sezóna → plugin hledal titulky ke 2. sezóně). Když datum
  vysílání dílu neodpovídá, vybere se položka AniListu podle data (u „Part 2“ uvnitř jedné sezóny se díl
  přečísluje od 1); když nesedí žádná, nenabídne se nic – raději nic než titulky k jiné sezóně.
- Vlastní sbírka: označení sezóny římskou číslicí i uprostřed názvu („Mushoku Tensei II  Isekai…“).
- Výsledek hledání ukazuje, ke kterému anime/sezóně titulky jsou („Mushoku Tensei III: … – díl 1 – Hns.sk [SubsPlease]“).

## 1.8.1 – 2026-10-14

- Vlastní sbírka: párování podle sezóny. Položka s označením sezóny (`-s2`, `Season 2`, `2nd Season`, `II`, `… 2`)
  slouží jen té sezóně; položka bez označení jen 1. sezóně – už ne 2. sérii, když ji AniList pojmenuje stejně
  jako 1. (Nisekoi 2 = „Nisekoi:“). Pro další sezóny se zkouší i „Název s2 / season 2 / 2“. Sbírka s více
  sezónami pod jedním názvem dál funguje, když soubory nesou `SxxEyy`.

## 1.8.0 – 2026-10-14

- Volitelně **hlášení z Jellyseerr / Overseerr**: hlášení typu *Titulky* doplněk každých 15 minut zpracuje – vyřadí
  uloženou verzi, stáhne jinou (nebo stejnou znovu srovná), odpoví v hlášení a volitelně ho označí jako vyřešené;
  když nic lepšího není, vrátí původní titulky. Zapíná se v nastavení (adresa + API klíč).
- Doplněk si pamatuje, kterou verzi titulků ke kterému videu uložil.

## 1.7.1 – 2026-10-14

- Šetrnost k webům: verze, ze které web nevydal použitelný soubor, se znovu zkouší až po *Znovu zkoušet
  zamítnuté po (dny)* (výchozí 14), ne každý den (vráceno z 1.6.1). Dočasné výpadky webu se zamítnutím nepočítají
  a zkouší se při dalším běhu jako dřív.

## 1.7.0 – 2026-10-14

- AI překlad přes **lokální AI**: Ollama (vlastní rozhraní – větší kontext, bez „přemýšlení“) nebo jakýkoli server
  s OpenAI rozhraním (LM Studio, llama.cpp, vLLM, LocalAI…). V nastavení adresa serveru a tlačítko *Načíst modely*.
  Menší dávky (≤ 100 řádků), filtr `<think>`, čekání až 30 minut, bez počítání útraty.
  Vlastní pole pro klíč lokálního serveru – klíč Claude/OpenAI se mu nikdy neposílá.
- Nastavení: pole pro Claude a lokální AI se ukazují jen u zvoleného překladače.

## 1.6.1 – 2026-10-13

- Nový zdroj **web.archive.org**: archivované stránky zaniklých skupin (gawsubs.com, ngfailsub.eu…) – soubor dílu,
  balík sezóny, nebo odkazy ke stažení v pořadí dílů. Pauza 5 s mezi dotazy; dočasná blokace archivu se nepočítá
  jako zamítnutí.
- Všechny weby: hlavičky `Accept` a `Accept-Language` jako u prohlížeče (bez nich je archive.org odmítal).

## 1.6.0 – 2026-10-13

- Nový zdroj **ange.3mka.cz** (3Mka): titulky po dílech, číslo dílu ověřené podle názvu souboru, balík všech dílů
  jako záloha.
- Nový zdroj **HannyaSubs** (hannya-subs.blogspot.com): soubory na mega.nz se stáhnou a rozšifrují přímo v doplňku,
  díl se ověří podle názvu souboru ještě před stažením.
- Řešení problémů: blokování webu DNS filtrem (Pi-hole/AdGuard).

## 1.5.2 – 2026-10-13

- Ruční hledání (*Upravit titulky → lupa*) se vždy znovu zeptá webu – dřív vracelo „Žádné výsledky“, dokud platil
  zapamatovaný výsledek „anime není na hiyori.cz“ (7 dní).
- Automatika si „nenalezeno na hiyori“ pamatuje jen 1 den místo 7.
- Stránka z hiyori načtená bez přihlášení (bez tabulky titulků) se neukládá do mezipaměti – krátký výpadek
  přihlášení tak nezablokuje hledání na celý den.

## 1.5.1 – 2026-10-13

- Historie: když automatika přeskočí díl, protože všechny verze titulků nedávno zamítla (nesedělo časování), zapíše
  `PŘESKOČENO` s důvodem – dřív zůstal jen řádek `HLEDÁNÍ` a vypadalo to jako zaseknutí.
- Před stažením se ověří, že do složky videa jde zapisovat (plný disk) – titulky se zbytečně nestahují a v historii
  je `NEULOŽENO` s důvodem.
- Po uložení se zkontroluje, že soubor u videa opravdu vznikl; jinak `CHYBA` v historii (Jellyfin chybu zápisu
  hlásí jen do svého logu).

## 1.5.0 – 2026-10-12

- AI: **nic se nezahazuje** – použitelné řádky z každé odpovědi (i rozbité nebo uříznuté) se hned ukládají, znovu se
  posílají jen chybějící řádky; přerušený překlad příště naváže.
- Dokumentace: doporučení modelů s cenami a dobou překladu (docs/ai-preklad.md).

## 1.4.1 – 2026-10-12

- AI: volba **„Překládat i automaticky“** (výchozí vypnuto) – jinak AI překlad jen ručně v *Hledat titulky*.
- AI: nastavitelná velikost dávky (výchozí 150 řádků; celý díl najednou = víc souvislostí, méně požadavků).

## 1.4.0 – 2026-10-12

- AI: **měsíční limit útraty** za API (výchozí 2 $) s odhadem podle spotřebovaných tokenů, zobrazení v nastavení.
- AI: vždy jen jeden překlad najednou; přeložené dávky se ukládají (přerušení neplatí dvakrát); nepoužitelná odpověď
  → dávka se rozdělí místo zahození celého dílu.
- OpenAI: výchozí model **gpt-5-mini** (výrazně levnější), `reasoning_effort: low`, `max_completion_tokens`.

## 1.3.3 – 2026-10-12

- Tlačítka *Vyzkoušet přihlášení*, *Prohledat sbírku* a *Vyzkoušet AI překlad* nejdřív uloží nastavení – testuje se, co je na obrazovce.
- OpenAI: modely GPT-5 / o-série běží s `reasoning_effort: low` – překlad je výrazně rychlejší a levnější.

## 1.3.2 – 2026-10-12

- Heslo k archivům kamui-subs.cz se zadává v nastavení u přihlášení na kamui (výchozí prázdné, heslo najdeš na jejich webu).

## 1.3.1 – 2026-10-12

- Bezpečnost: id výsledku z vlastní sbírky nese jen klíč položky, cesta se bere výhradně ze serverového indexu; kontrola, že soubor leží uvnitř nastavené složky, řeší symbolické odkazy a hranici složky.

## 1.3.0 – 2026-10-12

- **Vlastní sbírka titulků:** v nastavení zadáš složky (např. síťový disk), doplněk v nich hledá podsložky a archivy
  zip/rar (i s heslem a i sbírky s víc sériemi uvnitř), páruje je s názvem sezóny na AniList a díl bere z názvu souboru
  (s ohledem na SxxEyy). Titulky projdou stejným srovnáním časování. Volitelně přednost před weby, tlačítko *Prohledat sbírku*.

## 1.2.0 – 2026-10-12

- **Claude přes předplatné bez konzole:** v nastavení doplňku tlačítka *Nainstalovat Claude Code* (oficiální instalátor)
  a *Přihlásit se ke Claude* (odkaz → kód → token se uloží sám). Funguje i v Dockeru a mimo Proxmox.
- Překladače: Claude (předplatné nebo API klíč), OpenAI (jen API klíč) – proč, viz docs/ai-preklad.md.

## 1.1.1 – 2026-10-12

- Bezpečnost: id výsledků je podepsané tajným klíčem serveru (HMAC) – přes API nejde podvrhnout jiný odkaz ani spustit překlad.
- Bezpečnost: text titulků je pro AI jen data. Claude Code běží **bez jakýchkoli nástrojů** (`--tools ""`, bez MCP,
  bez uživatelských nastavení a hooků, v prázdné složce) – i podstrčené pokyny v titulcích nemůžou nic číst ani spouštět.
- Překladač ChatGPT (Codex CLI) odebrán: jeho příkazový řádek nejde vypnout, takže by šlo číst soubory na serveru.
  Pro OpenAI zůstává varianta s API klíčem (bez nástrojů).

## 1.1.0 – 2026-10-12

- **AI překlad** (volitelný): když k dílu nejsou české titulky nikde k nalezení a video má vložené titulky v jiném jazyce
  (přednostně anglické), doplněk je přeloží do češtiny. Překladače: **Claude přes předplatné claude.ai** (Claude Code CLI),
  **ChatGPT přes předplatné** (Codex CLI), nebo Claude/OpenAI API. Překládá se po scénách s kontextem (jména, oslovení,
  tykání/vykání), značky ASS zůstávají. Časování je převzaté z videa. Ukládá se jako `video.cs.ai.default.ass`
  a skutečný fansub ho později nahradí. Denní limit přeložených dílů, tlačítko *Vyzkoušet AI překlad*.

## 1.0.6 – 2026-10-12

- Bezpečnost: hledání střihů má pevné meze (nejvýš 3000 řádků a 8 kandidátních posunů), aby extrémní soubor nemohl zahltit server.

## 1.0.5 – 2026-10-12

- **Vzor pro časování v jakémkoli jazyce:** když video nemá anglické titulky, použije se jiná vložená stopa (francouzská, německá…)
  – porovnávají se jen začátky vět, ne text.
- **Nalezení střihů:** i když jeden posun sedí na většinu dílu, doplněk hledá místa, kde verze videa a titulků
  stříhají jinak (úvodní scéna, přestávka, upoutávka), a každý úsek posune zvlášť (segmentace Viterbi).
  Příklad Ao no Exorcist BD: úvodní scéna +9,9 s, zbytek −0,1 s.
- **Srovnání po částech i podle zvuku** – díl se rozdělí na ~10 částí, posun se změní jen tam, kde se na tom shodnou sousední části.
- **Vlastní titulky bez označení jazyka** (např. `video.ass`, `video.22edited.srt`) se poznají podle textu – díl se pak přeskočí.

## 1.0.4 – 2026-10-12

- Ohleduplnost k webům: **denní limit požadavků** na každý web (výchozí 500), titulky, které k dílu nesedí, se automaticky
  znovu nezkouší **14 dní**, seznam dílů z hns.sk se pamatuje 3 dny, tabulky z hiyori 24 h.
- Nové API `POST /CzAnimeTitulky/Upload/{id}` – nahrání vlastních titulků (ass/srt/zip/rar) k dílu se srovnáním časování.
- Oprava: odkazy na www.hns.sk (přihlášení platí pro hns.sk).
- Oprava: stará mezipaměť AniList bránila pokračování řetězce sezón za speciálem.

## 1.0.3 – 2026-10-12

- Nový zdroj: archiv **TeamNS** (teamns.gensubs.cz). hiyori odkazuje jen na úvodní stránku archivu; doplněk najde ZIP sezóny podle názvu z AniList (tolerantní porovnání, čísla sezón a „∬“ se musí shodovat) a vybere z něj díl.

## 1.0.2 – 2026-10-12

- Oprava: hns.sk – číslo dílu se na stránce anime nenašlo (text bez mezer), díly ze stránky anime se nestáhly.
- Oprava: řetězec sezón na AniList pokračuje i za speciálem/OVA (např. Dr. Stone S2 → Ryuusui → S3).

## 1.0.1 – 2026-10-12

- Bezpečnost: všechna připojení jen na veřejné adresy (i po přesměrování), jen zip/rar archivy, kontrola rozbalené velikosti.

## 1.0.0 – 2026-10-12

První verze.

- Poskytovatel titulků pro Jellyfin 12.1 (díly a filmy), automatický i ruční režim.
- Zdroje: hiyori.cz, hns.sk, kamui-subs.cz, wosir.cz, přímé odkazy; zip/rar s heslem.
- Párování přes AniList (řetězec pokračování = sezóny) a AniList odkaz na hiyori.cz.
- Výběr: CZ před SK, stejný release, BD/TV, konzistentní skupina, verze; v automatickém režimu až 3 náhrady.
- Srovnání časování podle anglických titulků ve videu (globálně, po částech, jiná snímková frekvence) a podle zvuku.
- Bezpečnost: stahuje se jen z daných webů, nikdy z místní sítě; limity velikosti stahování a archivů.
- Uložení jako `video.cs.default.ass`, historie činnosti, test přihlášení.
