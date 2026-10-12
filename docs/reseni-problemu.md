# Řešení problémů

Nejdřív se podívej do **historie** (*Doplňky → CZ Anime Titulky → Zobrazit posledních 300 záznamů*).
Jellyfin často loguje jen chyby, historie doplňku ukazuje všechno.

| Problém | Příčina / řešení |
|---|---|
| Doplněk není v seznamu stahovačů knihovny | Není načtený: *Doplňky* musí ukazovat **Active**. Po ruční instalaci restart Jellyfinu, zkontroluj vlastníka složky. Stav *NotSupported* = doplněk je pro jinou verzi Jellyfinu (viz [vyvoj.md](vyvoj.md#po-aktualizaci-jellyfinu)). |
| `CHYBA přihlášení` | Špatné údaje, nebo web změnil přihlašovací formulář. U kamui-subs.cz pomáhá přihlásit se jednou v prohlížeči (potvrzení účtu). |
| `NENALEZENO … anime není na hiyori.cz` | Seriál nemá AniList id (doplněk *AniList*, nebo *Identifikovat* seriál ručně), nebo ho hiyori nezná pod žádným z názvů. Ověř na hiyori.cz. |
| `NENALEZENO … díl N nemá CZ titulky na podporovaném webu` | Titulky ještě nevyšly, nebo jsou jen na nepodporovaném webu (mega.nz, hanabi.fan…). Doplněk to zkusí znovu při denní úloze. |
| Špatná sezóna | Jellyfin má jiné dělení sezón než AniList (např. „Season 2“ jsou ve skutečnosti 2 cour). Dej sezóně vlastní AniList id (*Upravit metadata → Externí ID → AniList*). |
| `NEULOŽENO … timing MISMATCH` | Titulky jsou pro jinou verzi videa a nejdou srovnat ani po částech. Zkus ručně jinou verzi v *Hledat titulky*, nebo jiný release videa. |
| Titulky jsou o kus mimo | U videí bez anglických titulků se srovnává podle zvuku; když si zvuk není jistý, titulky se uloží beze změny („unverified“). Ruční oprava: v přehrávači *Posun titulků*, nebo smaž soubor a vyber jinou verzi. |
| Titulky se nezapnou samy | Zkontroluj, že soubor končí `.cs.default.ass` (volba *Ukládat jako výchozí stopu*). Některé klienty (starší TV aplikace) příznak default ignorují – nastav uživateli *Jazyk titulků: Czech*. |
| Po upgradu videa (Sonarr) titulky zmizely | Sonarr přejmenuje/nahradí video a titulky se starým názvem osiří. Smaž je; doplněk při denní úloze stáhne nové a srovná je s novým videem. |
| Slovenské místo českých | Česká verze k tomu dílu na podporovaném webu není. Vypni *Když nejsou české, vzít slovenské*, pokud slovenské nechceš. |
| Úloha *Stáhnout chybějící titulky* trvá dlouho | U každého dílu se čte celé video (anglická stopa / zvuk, ~15–60 s přes síťový disk). Díly s titulky se přeskočí rychle. |

## Ladění

- Úroveň logování Jellyfinu: `logging.json` → `"MinimumLevel": "Information"` a pro jmenný prostor
  `Jellyfin.Plugin.CzAnimeTitulky` se objeví i podrobnější zprávy.
- API pro správce (hlavička `Authorization: MediaBrowser Token="<API klíč>"`):
  - `POST /CzAnimeTitulky/TestLogins` – test přihlášení,
  - `GET /CzAnimeTitulky/Activity?lines=300` – historie,
  - `GET /Items/<id dílu>/RemoteSearch/Subtitles/cze` – co by doplněk nabídl k danému dílu,
  - `POST /CzAnimeTitulky/Upload/<id dílu>?name=<soubor>` (tělo = soubor .ass/.srt/.zip/.rar) – vlastní titulky se srovnáním
    časování; `409`, když prokazatelně nesedí (`&force=true` uloží i tak).
- Mezipaměť lze smazat (`plugins/Jellyfin.Plugin.CzAnimeTitulky/cache/`) – vytvoří se znovu.
