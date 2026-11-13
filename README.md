# Mediathek für Jellyfin

Ein Plugin für [Jellyfin](https://jellyfin.org), das die Mediatheken der öffentlich-rechtlichen Sender in den eigenen Server holt:

- **Mediathek als Kanal**: Unter „Kanäle“ erscheint „Mediathek“ mit den Ordnern „Neu in den Mediatheken“, „Sender“ (je Sender die Sendungen, darin die Beiträge) und „Suche“ (je gemerktem Suchbegriff ein Ordner). Abgespielt wird die Datei des Senders direkt, in bis zu drei Qualitäten.
- **Live-TV**: Die Live-Programme der Sender laufen über das Live-TV von Jellyfin, mit Programmübersicht für die Programme der ARD-Familie, 3sat, arte, KiKA und phoenix.

Das Plugin braucht keine Zugangsdaten. Es fragt die Suche von [MediathekViewWeb](https://mediathekviewweb.de) ab (Beiträge und Stream-Adressen stammen aus der Filmliste von [MediathekView](https://mediathekview.de)) und die Programmübersicht der ARD. Es speichert keine Videos; jeder Client holt sie beim Abspielen vom Sender bzw. über den eigenen Server.

Stand: 0.1.0, erste lauffähige Fassung. Gebaut und geprüft gegen Jellyfin 12.4.

## Einrichten

1. In Jellyfin unter Dashboard → Plugins → Katalog → Einstellungen ein Repository mit der Adresse `https://raw.githubusercontent.com/derpfiff/jellyfin-mediathek/main/manifest.json` hinzufügen, im Katalog „Mediathek“ installieren und Jellyfin neu starten.
2. In den Einstellungen des Plugins „Live-TV einrichten“ drücken. Das trägt Senderliste und Programmübersicht im Live-TV ein.
3. Der Kanal „Mediathek“ erscheint, sobald Jellyfin seine Kanäle aktualisiert hat (geplante Aufgabe „Kanäle aktualisieren“, läuft auch von selbst).

Suchbegriffe legt man in den Einstellungen des Plugins an, einen je Zeile.

## Grenzen

- Viele Sendungen sind nur in Deutschland abrufbar. Steht der Server im Ausland oder hinter einem VPN, bleiben vor allem die Adressen des ZDF stumm.
- Jellyfin merkt sich den Inhalt eines Kanal-Ordners drei Stunden; Neues erscheint entsprechend verzögert.
- Für die Programme des ZDF (ZDF, ZDFneo, ZDFinfo) gibt es noch keine Programmübersicht, die Sender selbst laufen.
- Vorschaubilder zu den Beiträgen liefert die Filmliste nicht.

## Bauen

Die drei Bibliotheken des eigenen Servers nach `plugin/lib` kopieren (`MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll`, `MediaBrowser.Model.dll`, aus `/jellyfin` im Container), dann `sh plugin/build.sh`. Das Ergebnis liegt in `plugin/out/Jellyfin.Plugin.Mediathek.dll` und gehört in einen Ordner unter `plugins/` des Servers.

## Schnittstellen

| Adresse | Zweck |
|---|---|
| `GET /Mediathek/live.m3u` | Senderliste für den M3U-Tuner von Jellyfin |
| `GET /Mediathek/guide.xml` | Programmübersicht (XMLTV) |
| `GET /Mediathek/search?q=…` | Suche in den Mediatheken (angemeldete Nutzer) |
| `POST /Mediathek/searches?q=…` | Suchbegriff merken (Administratoren) |
| `POST /Mediathek/live/setup` | Live-TV einrichten (Administratoren) |

Dieses Projekt ist kein offizielles Angebot von Jellyfin, MediathekView oder einem der Sender.
