# Fletta

*fletta* ist isländisch für „blättern“. Schneller PDF-Betrachter für Windows. Ein Fenster pro PDF, Miniaturen, Zoom, Drehen mit
`R`/`L`, Suche, Links, passwortgeschützte PDFs, Drucken, Seite kopieren — und bearbeiten: Seiten löschen, umsortieren,
einfügen, in eine neue PDF kopieren oder verschieben; Anmerkungen (Textmarker, Notiz, Freihand, Text), Unterschrift
(gezeichnet oder aus Bild) und Formulare ausfüllen. Geändert wird die Datei erst mit `Strg+S`.

<img height="500" alt="image" src="https://github.com/user-attachments/assets/c97593a5-b6a1-4453-9a34-cfe2773217ac" />

## Technik

- C# / WPF auf .NET 10, abhängig von der installierten .NET Desktop Runtime.
- Rendern über [PDFium](https://pdfium.googlesource.com/pdfium/) aus
  [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), direkt per
  `LibraryImport`, ohne Wrapper-Bibliothek.
- Der Build lädt `pdfium-win-x64.tgz` selbst nach `lib/` und prüft den SHA-256 gegen
  `src/Fletta.csproj`. Die Lizenztexte von PDFium landen unter `licenses/` neben der EXE.
- Der Druckdialog kommt aus `Windows.Graphics.Printing` (deshalb das Zielframework
  `net10.0-windows10.0.19041.0`); Vorschau und Ausgabe zeichnen über Direct3D und Direct2D,
  angebunden mit [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows). Geladen wird
  das alles erst beim ersten `Strg+P`.

## Bauen und prüfen

Nur unter Windows, mit dem .NET-10-SDK:

```powershell
powershell -ExecutionPolicy Bypass -File tools\check.ps1                 # Build + Fletta.exe --selftest
powershell -ExecutionPolicy Bypass -File tools\measure.ps1 -Pdf <datei>  # Publish (ReadyToRun) + Startzeit messen
```

## Aufruf

```
Fletta.exe [datei.pdf]
Fletta.exe --selftest        Selbsttest, Exit-Code = Zahl der Fehler
Fletta.exe --measure datei   öffnet, rendert, schreibt die Zeitmarken auf stderr, schließt
```

| Taste | Wirkung |
|---|---|
| `R` / `L` | rechts / links drehen: markierte Miniaturen, sonst die aktuelle Seite (Ansicht; `Strg+S` schreibt es in die Datei) |
| Rechtsklick auf Miniaturen | drehen, löschen, in neue PDF kopieren oder verschieben, PDF davor oder dahinter einfügen |
| Mausrad über den Miniaturen | scrollt das Dokument, die Leiste folgt der aktuellen Seite |
| Miniaturen ziehen | umsortieren; hinaus in den Explorer oder ein anderes Fenster kopiert sie als neue PDF, mit Umschalt verschoben |
| PDF auf die Miniaturen ziehen | ihre Seiten an dieser Stelle einfügen |
| Entf | markierte Miniaturen löschen, sonst die aktuelle Seite |
| Strg+F, dann Enter oder F3 | suchen; nächster Treffer, mit Umschalt voriger; Esc schließt die Suche |
| Klick auf einen Link | zur Zielseite springen oder Web-/Mail-Adresse im Standardprogramm öffnen |
| Werkzeuge oben: Textmarker, Notiz, Freihand, Text, Unterschrift | Text überstreichen, an eine Stelle klicken oder mit gedrückter Maustaste zeichnen; Esc beendet das Werkzeug |
| Rechtsklick auf eine Anmerkung | Notiz bearbeiten oder Anmerkung löschen |
| Klick in ein Formularfeld | ausfüllen: Enter übernimmt, Tab springt zum nächsten Feld, Esc verwirft; Kästchen und Listen per Klick |
| Strg+Z | letzte Änderung zurücknehmen |
| Strg+S | Änderungen und Drehungen speichern; die Datei wird vollständig neu geschrieben |
| F4 oder Knopf oben links | Seitenleiste mit Miniaturen und Gliederung ein/aus (Rand ziehen ändert die Breite) |
| B | Doppelseite wie ein Buch ein/aus (Deckblatt einzeln, danach Paare) |
| S | seitenweise blättern ein/aus: Mausrad (je Rastung), Bild↓ und Leertaste gehen eine Seite (ein Paar) weiter; ist die Seite höher als das Fenster, erst bis zu ihrem Rand |
| Strg+A | alle Seiten markieren; Strg/Umschalt-Klick markiert einzelne oder Bereiche |
| `+` / `−`, Strg+Mausrad | Zoom in Stufen (25–400 %) |
| Strg+0 / Strg+1 / Strg+2 | ganze Seite / 100 % / Seitenbreite |
| ← / → | vorige / nächste Seite |
| Pos1 / Ende | erste / letzte Seite |
| ↑ ↓, Bild↑ Bild↓, Leertaste (mit Umschalt zurück) | scrollen |
| linke Maustaste halten und ziehen | Ansicht verschieben |
| F1 oder `?` | Übersicht der Tastenkürzel |
| Strg+O | öffnen |
| Strg+P | drucken (Windows-Druckdialog mit Vorschau: alle Seiten oder Bereiche, markierte Miniaturen vorbelegt) |
| Strg+C | aktuelle Seite als Bild (200 dpi) und Text in die Zwischenablage |
| Strg+Umschalt+C | nur den Text der aktuellen Seite |
| Esc | Menü, Suche, Werkzeug oder Mehrfachauswahl schließen, sonst Fenster schließen |

Änderungen sammelt Fletta, bis gespeichert wird; der Fenstertitel zeigt dann „●“, oben rechts steht ein
Speichern-Knopf, und beim Schließen
fragt Fletta nach. Beim Speichern verschwinden gelöschte Seiten wirklich aus der Datei. Lesezeichen
bleiben erhalten, zeigen aber ins Leere, wenn ihre Seite gelöscht wurde; digital signierte PDFs verlieren
beim Speichern ihre Signatur (Fletta warnt vorher).

PDFs lassen sich auch ins Fenster ziehen; ist schon eines offen, startet für jedes weitere ein
eigenes Fenster. Die aktuelle Seite steht im Fenstertitel und unten rechts; dort springt eine
eingetippte Nummer mit Enter hin. Unten links wählt ein Klick auf die Zoomanzeige eine Stufe.

## Installieren

`Fletta-X.Y.Z-setup.exe` aus den [Releases](https://github.com/martinhoess/fletta/releases) starten:
installiert ohne Adminrechte nach `%LOCALAPPDATA%\Programs\Fletta`, bringt die .NET-Runtime mit
und meldet Fletta als PDF-Programm an. Zum Entwickeln geht es auch ohne Setup:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1   # Publish nach %LOCALAPPDATA%\Programs\Fletta, Anmeldung als PDF-Programm
```

Danach öffnet sich „Standard-Apps“: dort `.pdf` auf Fletta stellen — den Standard darf unter
Windows nur der Benutzer setzen. Abmelden: `Fletta.exe --unregister`.

Updates: Fletta fragt höchstens einmal am Tag bei GitHub nach der neuesten Version. Gibt es eine,
steht oben rechts „Aktualisieren“. Der Knopf „Updates“ in der Werkzeugleiste prüft sofort und schaltet
Vorabversionen (Tag `vX.Y.Z-pre`) zu — dann fragt Fletta stündlich und bietet auch diese an. Der Knopf lädt das Setup, prüft es gegen die SHA-256-Summe aus
dem Release und installiert es. Offene Fenster gehen dabei zu und kommen mit ihrer PDF wieder.

Fletta merkt sich Fensterlage, Seitenleiste, Zoommodus, Doppelseite und seitenweises Blättern
unter `%APPDATA%\Fletta\settings.json`.

## Lizenz

WTFPL, siehe `LICENSE`. PDFium steht unter eigener Lizenz (BSD-3-Clause und Apache-2.0,
Texte unter `licenses/` im Build).
