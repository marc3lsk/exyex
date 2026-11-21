# exyex – roadmap / nápady

Legenda: ✅ hotové · 🟡 rozpracované / čiastočné · ⬜ nápad

## Rýchle výhry
- ✅ **Asynchrónne načítanie priečinkov** – pomalé / sieťové priečinky už nezasekávajú UI (starý obsah zostane, naviguje sa na pozadí).
- ✅ **Preusporiadanie tabov ťahaním** v rámci jedného panelu.
- ✅ **Zapamätanie okien** – pozícia a veľkosť hlavného okna aj okna operácií (aj maximalizované).
- ⬜ **„Open in exyex" v kontextovom menu Explorera** (záznam v registri, pridať do inštalátora).
- ✅ **Klávesové skratky** pre PowerShell / cmd / Explorer tu, pridanie do favorites z návrhov (`Ctrl+B`).

## Veľké funkcie
- ✅ **Quick Look** (`Space`) – náhľad textu, obrázkov, priečinkov a metadát v bočnom paneli.
- ⬜ **Hromadné premenovanie** – vzor `foto_{n:3}.jpg`, regex náhrada, živý náhľad.
- ✅ **Porovnanie a synchronizácia panelov** – zvýraznenie rozdielov, výber rozdielov, kopírovanie do druhého panelu.
- ✅ **Archívy ako priečinky** – zip/jar sa dajú prechádzať, vyhľadávať v nich a kopírovať von (len na čítanie).
- ✅ **SFTP cez Pageant** – pripojenie (`sftp://user@host`), listing, kopírovanie / presun / mazanie / premenovanie / nový priečinok, otvorenie súboru. Hotové fázy 1–3; ďalej: editácia na mieste s uploadom, remote → remote, synchronizácia priečinkov, resume prenosov.

## Hľadanie
- ✅ Smart / Fuzzy / Wildcard / Regex, hľadanie v podpriečinkoch, manuálny exclude.
- ✅ **Hľadanie podľa obsahu súborov** (grep) v podpriečinkoch – čip *Content* / `Alt+T`, úryvok prvého riadku.
- ✅ **Filtre podľa veľkosti, dátumu a typu** – `:size>10mb`, `:age<7d`, `:type=img` (dvojbodka na začiatku).
- ⬜ **Uložené hľadania.**

## Používanie
- ⬜ **Workspaces** – uložená sada tabov v oboch paneloch, prepínanie jedným príkazom.
- ⬜ **Veľkosti priečinkov** v stĺpci Size (počítané na pozadí) + prehľad využitia disku.
- ✅ **Iné zobrazenia** – Details / Compact / Grid s náhľadmi obrázkov, voliteľné stĺpce (Type, Size, Modified).
- ⬜ **Undo poslednej operácie** (presun / premenovanie) + história operácií.

## Kvalita
- ⬜ **Testy UI a integračné testy** (inštalátor, drag & drop, kontextové menu nie sú overené v praxi).
- ⬜ **Lepšie chybové hlášky** (napr. „access denied" pri kopírovaní) namiesto krátkej správy v stavovom riadku.
- ⬜ **Diagnostika** – logovací súbor a „copy debug info".
- ⬜ **Rekurzia pre `rename` v hľadaní podpriečinkov** (teraz sa výsledky z podpriečinkov nedajú premenovať na mieste).
