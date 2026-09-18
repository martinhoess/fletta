# Änderungen

Die Release-CI übernimmt den Abschnitt der jeweiligen Version als Text des GitHub-Releases.

## 0.13.0

- Suchen mit Strg+F: Treffer sind auf den Seiten markiert, Enter oder F3 springt zum nächsten, mit Umschalt zurück. Den Text großer PDFs liest Fletta beim ersten Suchen im Hintergrund, danach ist jede Suche sofort da.
- Links: Klick springt zur Zielseite oder öffnet Web- und Mail-Adressen im Standardprogramm, auch Adressen, die nur als Text dastehen. Andere Links (Programme, Dateien) öffnet Fletta nicht.
- Passwortgeschützte PDFs fragen beim Öffnen nach dem Passwort; Speichern behält den Schutz.
- Anmerkungen über die neuen Werkzeuge oben: Textmarker, Notiz, Freihand und Text auf die Seite (Größe wählbar). Rechtsklick auf eine Anmerkung bearbeitet eine Notiz oder löscht sie.
- Unterschrift: einmal mit Maus oder Stift zeichnen oder aus einem Bild laden (heller Hintergrund wird durchsichtig), Fletta merkt sie sich; ein Klick setzt sie, ein gezogener Rahmen bestimmt die Breite.
- Formulare ausfüllen: Klick in ein Feld, tippen, Enter übernimmt, Tab springt zum nächsten Feld; Kästchen, Optionsfelder und Auswahllisten per Klick. Ausfüllbare Felder sind hellblau hinterlegt.
- Alle Anmerkungen und Formulareingaben sind Schritte wie das Löschen von Seiten: Strg+Z nimmt sie zurück, Strg+S speichert.
- Drucken lässt Anmerkungen und Formularfelder weg, die nicht zum Drucken gedacht sind (etwa einen „Drucken“-Knopf im Formular).

## 0.12.3

- Miniaturen sind beim Durchscrollen großer PDFs schneller da: vorab gerendert wird vor allem in Scrollrichtung, und die Miniatur einer Seite entsteht direkt nach der Seite selbst, solange sie noch geladen ist.

## 0.12.2

- Die Miniaturenleiste folgt der aktuellen Seite zuverlässig: die aktuelle Miniatur steht immer ganz im Bild, auch am Dokumentende.

## 0.12.1

- Mausrad über den Miniaturen scrollt das Dokument; die Leiste folgt der aktuellen Seite.
- Ungespeicherte Änderungen zeigt ein Speichern-Knopf oben rechts in der Werkzeugleiste.
- Rückfragen („Änderungen speichern?“, Warnung bei signierten PDFs) im Stil von Fletta statt der hellen Windows-Meldung.
- Setup: Umlaute statt Umschreibungen („Standard-Apps öffnen“).

## 0.12.0

- Seiten bearbeiten über die Miniaturen: löschen, drehen, umsortieren, in eine neue PDF kopieren oder verschieben, eine PDF davor oder dahinter einfügen – per Rechtsklick-Menü oder Ziehen und Ablegen.
- Seiten aus der Leiste in den Explorer oder ein anderes Fletta-Fenster ziehen (Umschalt verschiebt).
- Änderungen werden gesammelt: `Strg+Z` nimmt zurück, `Strg+S` schreibt sie samt Drehungen in die Datei.
- Rückfrage beim Schließen mit ungespeicherten Änderungen, Warnung vor dem Speichern signierter PDFs.

## 0.11.0

- Update über GitHub: Fletta sieht höchstens einmal täglich nach, „Aktualisieren“ lädt das Setup, prüft die Prüfsumme und öffnet die PDFs danach wieder.

## 0.10.1

- Weniger Speicher bei starkem Zoom: weniger vorgerenderte Nachbarseiten, verworfene Seitenbilder werden freigegeben.

## 0.10.0

- Drucken über den Windows-Druckdialog mit echter Vorschau und Seitenbereichen; markierte Miniaturen sind vorbelegt.
- Speicherlecks behoben: Seiten und ausgeblendete Miniaturen halten ihre Bilder nicht mehr fest.

## 0.9.1

- Setup: offene Fenster werden beim Deinstallieren beendet, der Programmordner bleibt nicht mehr liegen.

## 0.9.0

- Erste Version: ein Fenster pro PDF, Seitenleiste mit Miniaturen und Gliederung, Zoom, Drehen in der Ansicht, Doppelseite und seitenweises Blättern, Drucken, Seite als Bild und Text kopieren.
