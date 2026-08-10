# BitCraft Overlay

Pływająca, zawsze-na-wierzchu nakładka dla [BitCraft Online](https://bitcraft.game), która pokazuje w jednym miejscu, bez wychodzenia z gry i bez zmiany focusa okna:

- **[bitcraftsync.app](https://bitcraftsync.app)** — Twój share-link drużyny
- **[bitjita.com](https://bitjita.com)** — rynek/oferty
- **[brico.app](https://brico.app)** — baza receptur
- **[bitcraftmap.com](https://bitcraftmap.com)** — mapa (zapamiętuje ostatnią widoczną pozycję)
- **Twitch** (`twitch.tv/bitcraftonline`) — osobne okno do oglądania streamu / dropów, z przyciskiem wyciszenia

## Funkcje

- Przeciągalny pasek (zakładki + przyciski) zawsze w 100% widoczny, niezależnie od ustawień przezroczystości
- Suwak przezroczystości treści 0-90% (nigdy nie znika całkowicie)
- Zwijanie do samej belki, reset rozmiaru, zmiana szerokości/wysokości uchwytem w rogu
- Zapamiętuje pozycję, rozmiar i ostatni URL każdej zakładki
- Możliwość ukrycia wybranych zakładek w ustawieniach

## Build

Wymaga [.NET 8 SDK](https://dotnet.microsoft.com/download) i Windows (WPF + WebView2 Runtime, zwykle preinstalowany na Windows 11).

```bash
cd BitCraftOverlay
dotnet build
dotnet run
```

## Status

Projekt hobbystyczny, tworzony na potrzeby społeczności BitCraft. Zgłoszenia błędów / pomysły mile widziane przez Issues.
