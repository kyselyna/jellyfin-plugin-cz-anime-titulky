# Instalace

## Varianta A – z katalogu doplňků (doporučeno, jen pro veřejný repozitář)

Jellyfin umí instalovat a aktualizovat doplňky z vlastního repozitáře. Funguje to jen tehdy,
když je tento GitHub repozitář **veřejný** (Jellyfin se nepřihlašuje na GitHub).

1. *Ovládací panel → Doplňky → Repozitáře → +*
2. Název: `CZ Anime Titulky`
   URL: `https://raw.githubusercontent.com/kyselyna/jellyfin-plugin-cz-anime-titulky/main/manifest.json`
3. *Katalog → CZ Anime Titulky → Instalovat*
4. Restartuj Jellyfin.

Nové verze se pak nabídnou samy (a s automatickými aktualizacemi doplňků se i samy nainstalují).

## Varianta B – ručně ze ZIPu

1. Stáhni `cz-anime-titulky_<verze>.zip` z [Releases](https://github.com/kyselyna/jellyfin-plugin-cz-anime-titulky/releases).
2. Na serveru vytvoř složku `plugins/CZ Anime Titulky_<verze>` v datovém adresáři Jellyfinu:
   - Debian/Ubuntu (balíček): `/var/lib/jellyfin/plugins/`
   - Docker: `/config/plugins/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\`
3. Rozbal do ní obsah ZIPu (`Jellyfin.Plugin.CzAnimeTitulky.dll`, `AngleSharp.dll`).
4. Nastav vlastníka na uživatele, pod kterým Jellyfin běží (Linux: `chown -R jellyfin:jellyfin "…/CZ Anime Titulky_<verze>"`).
5. Restartuj Jellyfin (`systemctl restart jellyfin`).
6. V *Ovládací panel → Doplňky* musí být **CZ Anime Titulky – Active**.

## Varianta C – sestavení ze zdrojáků

Viz [vyvoj.md](vyvoj.md).

## Odinstalace

*Ovládací panel → Doplňky → CZ Anime Titulky → Odinstalovat* a restart. Už stažené titulky (soubory vedle videí) zůstanou.
