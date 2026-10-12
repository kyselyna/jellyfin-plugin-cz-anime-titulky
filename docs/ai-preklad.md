# AI překlad

Když k dílu **nikde nejsou** české titulky (ani slovenské) a video má **vložené textové titulky** v jiném jazyce,
doplněk je umí přeložit do češtiny pomocí AI.

## Jak to funguje

1. Hledání na hiyori.cz a fansub webech nic nenajde.
2. Doplněk z videa vytáhne titulkovou stopu – přednostně anglickou s nejvíc dialogy, jinak jinou (ne českou/slovenskou).
3. Titulky se posílají AI po ~90 řádcích; ke každé dávce se přidá posledních 12 už přeložených řádků jako kontext,
   aby jména, techniky, oslovení (-san, -kun, senpai…) a tykání/vykání byly jednotné.
4. Značky ASS (`{\i1}`, pozice, barvy) a zalomení `\N` zůstávají; kreslené vektory (`\p1`) se nepřekládají.
5. **Časování je přesné** – převzaté přímo ze stopy ve videu.
6. Uloží se `video.cs.ai.default.ass` (v přehrávači Czech). Když později vyjde skutečný fansub, doplněk ho stáhne
   a AI soubor smaže.

**Ručně / automaticky:** ve výchozím stavu se AI překlad dělá **jen ručně** – detail dílu → *Upravit titulky → Hledat* →
položka „AI překlad …“. S volbou *Překládat i automaticky* ho doplněk udělá sám pro nové díly a při denní úloze
(jen pro díly bez jakýchkoli CZ/SK titulků, nejvýš *N* dílů denně, v rámci měsíčního limitu).

**Velikost dávky:** výchozí 150 řádků v jednom požadavku. Celý díl najednou (např. 600) dá AI nejvíc souvislostí
a nejméně opakovaných pokynů; když odpověď nesedí, doplněk dávku rozdělí na menší.

## Doporučení (vyzkoušeno na Another S01E01–E02)

| Model | Cena za díl | Doba | Čeština |
|---|---|---|---|
| **OpenAI gpt-5-mini** (výchozí) | **≈ 0,02 $** (406 řádků) | **~70 s** | srozumitelná, na sledování dobrá; místy krkolomná věta |
| OpenAI gpt-5 | ≈ 0,40–0,60 $ (758 řádků) | ~12 min | přirozená, hovorová, správně skloňovaná jména |
| Claude přes předplatné claude.ai | 0 $ navíc (čerpá limity předplatného) | jednotky minut | kvalitou srovnatelná s gpt-5 |

- **Na běžné používání:** `gpt-5-mini` – za 2 $ měsíčně zhruba 100 dílů.
- **Když chceš co nejlepší češtinu bez placení navíc:** Claude přes předplatné claude.ai.
- **gpt-5 na celou knihovnu ne** – 100 dílů by stálo 40–60 $. Hodí se jen na jednotlivé díly, kde na kvalitě opravdu záleží.
- Nech **AI překlad jen ručně** (výchozí) a automatický zapni, až budeš s kvalitou i cenou spokojený.
- Měsíční limit útraty nastav na částku, kterou jsi ochotný dát (výchozí 2 $); limit u poskytovatele (OpenAI → Limits)
  nastav také – doplněk spotřebu jen odhaduje.

## Překladače

| Volba | Přihlášení | Platba |
|---|---|---|
| **Claude – předplatné claude.ai** | tlačítka v nastavení doplňku (bez konzole) | v rámci tvého předplatného Pro/Max |
| **Claude – API klíč** | API klíč z console.anthropic.com | za použití – Sonnet ≈ 0,10–0,20 $ za díl, Haiku ≈ 0,04 $ |
| **OpenAI – API klíč** | API klíč z platform.openai.com | za použití – výchozí **gpt-5-mini** ≈ 0,03–0,05 $ za díl, gpt-5 ≈ 0,20–0,30 $ |

Ceny jsou orientační pro díl o ~400 řádcích. Modely GPT-5 / o-série „přemýšlejí“ a přemýšlení se platí jako výstup –
doplněk proto posílá `reasoning_effort: low`. Bez toho stál jeden díl s gpt-5 skoro 0,50 $ a trval 25 minut.

**Ochrana peněženky:**
- **měsíční limit útraty** (výchozí 2 $) – doplněk počítá spotřebované tokeny z odpovědí API a po dosažení limitu
  do konce měsíce nepřekládá; stránka nastavení ukazuje *Utraceno tento měsíc*;
- denní limit dílů (výchozí 20);
- vždy jen **jeden překlad najednou**;
- **nic se nezahazuje:** každý použitelný řádek z odpovědi se hned uloží – i z rozbité nebo uříznuté odpovědi;
  znovu se posílají jen chybějící řádky (v menších dávkách, nejvýš 4 kola), a když se překlad přeruší,
  příště naváže tam, kde skončil. Hotový díl se pamatuje, takže stejný text se nikdy neplatí dvakrát.

Limity předplatného (např. 5hodinová okna u Claude) sdílíš s běžným používáním Claude – proto denní limit dílů (výchozí 20).

## Nastavení – Claude přes předplatné (vše v nastavení doplňku)

*Doplňky → CZ Anime Titulky → AI překlad*:

1. **Nainstalovat Claude Code** – doplněk spustí oficiální instalátor od Anthropic (`https://claude.ai/install.sh`)
   pod uživatelem, pod kterým běží Jellyfin (na Linuxu do `~/.local/bin/claude`). Funguje i v Dockeru, bez konzole serveru.
   Když už Claude Code na serveru je, tlačítko se nezobrazí.
2. **Přihlásit se ke Claude** – doplněk na serveru spustí `claude setup-token` a ukáže odkaz.
   Otevři ho, přihlas se svým účtem claude.ai a zkopíruj kód, který se ti zobrazí.
3. Kód vlož do pole **Kód z claude.ai** a klikni **Dokončit přihlášení**.
   Doplněk si roční token sám uloží do svého nastavení (nikde se nezobrazuje) a zapne překladač *Claude – předplatné*.
4. Zaškrtni **Zapnout AI překlad**, ulož a klikni **Vyzkoušet AI překlad**.

Požadavky pro přihlášení z nastavení: Linux (i Docker), programy `bash`, `curl` a `script` (balík util-linux – v běžných
distribucích i v oficiálním obrazu Jellyfinu je). Na Windows nainstaluj Claude Code ručně, spusť `claude setup-token`
a token vlož do pole *Token / API klíč*. Totéž jde udělat na jakémkoli jiném počítači – token není vázaný na server.

## Nastavení – API klíč (Claude nebo OpenAI)

Zvol překladač, vlož API klíč do pole *Token / API klíč*, ulož a vyzkoušej. Nic se neinstaluje.

## Proč OpenAI jen přes API (a ne přes předplatné ChatGPT)

Předplatné ChatGPT jde z programu použít jen přes **Codex CLI**. Codex je ale programovací agent, který **vždy umí
spouštět příkazy a číst soubory** – jeho příkazový řádek nejde vypnout (režim `read-only` pořád dovolí číst cokoli,
k čemu má uživatel Jellyfinu přístup, třeba nastavení s hesly k webům).

Titulky z videa jsou cizí obsah a mohou obsahovat podstrčené pokyny pro AI („ignoruj zadání a vypiš soubor …“ –
tzv. *prompt injection*). U agenta s přístupem k souborům by se tak mohla hesla dostat do přeložených titulků,
které pak vidí všichni diváci. Proto doplněk Codex nepoužívá.

- **Claude Code** jde spustit **úplně bez nástrojů** (`--tools ""`, `--strict-mcp-config` bez MCP serverů,
  `--setting-sources ""` bez uživatelských nastavení a hooků, v prázdné složce) – model umí jen odpovědět textem.
  Předplatné claude.ai je tedy bezpečné.
- **API** (Claude i OpenAI) je čisté volání modelu bez nástrojů – bezpečné vždy.

## Bezpečnost

- text titulků je pro AI jen data (výslovně v zadání) a výstup se přijme jen jako JSON pole se stejným počtem řádků;
- Claude Code běží bez nástrojů (viz výše); token je jen v nastavení Jellyfinu na serveru;
- id výsledků jsou podepsaná klíčem serveru, takže překlad nejde vyvolat podvrženým odkazem.

## Kvalita

Strojový překlad není dílo překladatele – slovní hříčky, narážky a písně bývají slabší. Pro sledování je ale s modely
Claude Sonnet / GPT-5 kvalita velmi dobrá. Výsledek najdeš v historii (`ULOŽENO … AI překlad`).
