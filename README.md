# Mediathek für Jellyfin

Ein Plugin für [Jellyfin](https://jellyfin.org), das die ARD Mediathek und die Live-Programme der öffentlich-rechtlichen Sender in den eigenen Server holt:

- **Mediathek als Kanal**: Unter „Kanäle“ erscheint „Mediathek“ mit den Reihen, die die Mediathek gerade auf ihrer Startseite zeigt (Serien, Filme, Dokus, …), und einer Suche. Serien haben Staffeln und Folgen, alles mit Plakaten, Bildern und Beschreibungen.
- **Live-TV**: Die Live-Programme laufen über das Live-TV von Jellyfin, mit Programmübersicht samt Bildern für die Programme der ARD-Familie, ZDF, ZDFneo, ZDFinfo, 3sat, arte, KiKA und phoenix. Sender aus dem Ausland erscheinen nur, wenn der Server sie erreicht.
- **Über den eigenen Server**: Auf Wunsch (Voreinstellung) laufen Videos und Live-Sender durch den Server. Für Ländersperren zählt dann sein Standort, und es lässt sich wie gewohnt im Video springen.

Das Plugin braucht keine Zugangsdaten und speichert keine Videos. Titel, Bilder und Videoadressen kommen von der ARD Mediathek, die Adressen der Live-Sender aus der Filmliste von [MediathekView](https://mediathekview.de) über [MediathekViewWeb](https://mediathekviewweb.de).

Stand: 0.2.2. Gebaut und geprüft gegen Jellyfin 12.4.

## Einrichten

1. In Jellyfin unter Dashboard → Plugins → Katalog → Einstellungen ein Repository mit der Adresse `https://raw.githubusercontent.com/derpfiff/jellyfin-mediathek/main/manifest.json` hinzufügen, im Katalog „Mediathek“ installieren und Jellyfin neu starten.
2. In den Einstellungen des Plugins „Live-TV einrichten“ drücken. Das trägt Senderliste und Programmübersicht im Live-TV ein; ein zweiter Druck legt nichts doppelt an.
3. Der Kanal „Mediathek“ erscheint, sobald Jellyfin seine Kanäle aktualisiert hat (geplante Aufgabe „Kanäle aktualisieren“, läuft auch von selbst).

Das Theme [Cinema Glass](https://github.com/derpfiff/cinema-glass) fasst Live-TV und Mediathek ab Version 1.4.0 zu einer eigenen TV-Seite zusammen; nötig ist es nicht.

## Grenzen

- Einzelne Titel sind nur in Deutschland abrufbar. Steht der Server im Ausland, hilft die Einstellung „über diesen Server leiten“ nicht; steht er in Deutschland (oder hinter einem deutschen VPN), laufen sie überall.
- Titel, die es nur als gestückelten Stream gibt (kein MP4), fehlen noch.
- Jellyfin merkt sich den Inhalt eines Kanal-Ordners eine Weile; das Plugin lässt die Startseite stündlich neu laden.
- Die Mediathek des ZDF ist noch nicht dabei; seine Sender und ihr Programm schon.
- Für Deutsche Welle und arte (französisch) gibt es keine Programmübersicht.

## Bauen

Vier Bibliotheken des eigenen Servers nach `plugin/lib` kopieren (`MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll`, `MediaBrowser.Model.dll`, `Jellyfin.Database.Implementations.dll`, aus `/jellyfin` im Container), dann `sh plugin/build.sh`. Das Ergebnis liegt in `plugin/out/Jellyfin.Plugin.Mediathek.dll` und gehört in einen Ordner unter `plugins/` des Servers. `sh plugin/package.sh` packt es zusätzlich für den Katalog.

## Schnittstellen

Für Oberflächen, die die Mediathek selbst darstellen wollen. Die ersten vier verlangen eine Anmeldung.

| Adresse | Zweck |
|---|---|
| `GET /Mediathek/home` | Reihen der Startseite (Kennung, Titel, Anzahl) |
| `GET /Mediathek/row?id=…` | Titel einer Reihe, als Jellyfin-Titel |
| `GET /Mediathek/search?q=…` | Suche in der Mediathek, als Jellyfin-Titel |
| `POST /Mediathek/live/setup` | Live-TV einrichten (Administratoren) |
| `GET /Mediathek/live.m3u` | Senderliste für den M3U-Tuner von Jellyfin |
| `GET /Mediathek/guide.xml` | Programmübersicht (XMLTV) |
| `GET /Mediathek/live/…`, `/Mediathek/vod/…`, `/Mediathek/img/…` | Live-Sender, Videos und Bilder, durch den Server gereicht |

Durchgereicht wird nur, was der Server selbst ausgegeben hat: jede dieser Adressen trägt eine Prüfsumme mit einem Schlüssel, den das Plugin beim ersten Start würfelt. Als offener Proxy lässt es sich nicht benutzen.

Dieses Projekt ist kein offizielles Angebot von Jellyfin, der ARD, MediathekView oder einem der Sender.
