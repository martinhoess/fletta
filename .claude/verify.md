# fletta — Verify-Kontrakt

Diese Datei sagt Prüf-Agents (`mother`, `ash`), was in diesem Repo gilt.
Sie ist der Kontrakt: was hier nicht steht, wird nicht geraten.

Das Repo ist öffentlich. Was an den eigenen Rechnern hängt — Bauweg vom Linux-Container aus, Test-VM für die
Sichtprüfung, Hosts, Pfade, Hilfsskripte — steht auf der Wiki-Seite `fletta` im Abschnitt „Prüfweg (privat)“, nicht hier.
Die Hilfsskripte dafür liegen nicht im Repo (`.git/info/exclude`); Nachweis:
`git ls-files tools/sync.ps1 tools/tunnel_job.py tools/vm_check.py` muss leer sein.

## Prüfen (führt `mother` aus)

Baut und läuft **nur auf Windows** mit dem .NET-10-SDK. Im Linux-Container ist beides nicht möglich —
dort ist ein Skip kein Fehler.

```powershell
powershell -ExecutionPolicy Bypass -File tools\check.ps1
```

`check.ps1` baut Release und startet `Fletta.exe --selftest`. Ergebnis ist die letzte Zeile
`selftest exit: N`, 0 = grün. Darüber steht je Prüfung `ok` oder `FEHLER`. `check.ps1`, `measure.ps1` und
`install.ps1` bauen den Ordner über `tools/` — aus einer lokalen Arbeitskopie starten, nicht von einer Netzfreigabe.
Fehlen neue Prüfungen in der Ausgabe, hielt MSBuild den Build womöglich für aktuell (Quelldateien älter als die
zuletzt gebaute DLL) — dann neu bauen.

Der Selbsttest braucht kein Fenster. Er schreibt ein kleinstes PDF (A4, blaues Rechteck) in
einen Pfad mit Leerzeichen und Umlauten und prüft:

- `PdfDocument`: Seitenzahl (zwei Seiten), Seitengröße in Punkten, dass `/Rotate 90` im PDF
  die Kanten von `PageSize` tauscht (davon hängt ab, dass Flettas eigene Drehung nur obendrauf
  kommt), weißer Rand, blaue Mitte und rotes Quadrat oben links nach dem Rendern, rechts
  gedreht liegt das Quadrat oben rechts (Drehrichtung), Fehlercode bei fehlender Datei
  (`FileError`) und bei Datei ohne PDF-Inhalt (`FormatError`).
- `PageLayout`: Seitenoberkanten samt Abstand und Rändern, `PageAt` an den Grenzen, Maßstab
  für Seitenbreite, ganze Seite und 100 %, Zoomstufen hoch/runter/zwischen zwei Stufen/an den
  Enden.
- `PageLayout` Doppelseite (Zeilen 1 | 2 3 | 4 5): Paar in einer Zeile, Abstand im Paar,
  Deckblatt mittig, Breite, ← → um eine Zeile, sichtbare Seiten, Seitenbreite passt das Paar
  ein; seitenweise: je Zeile eine Sichthöhe, Seite mittig, Sprungziel, Platz einer Zeile.
- `PageLayout.KeepPosition` (Lesestelle beim Zoomen): gleiche Stelle bei gleichem Layout, wächst
  mit dem Zoom mit, seitenweise steht die Zeile nach dem Verkleinern wieder ganz im Bild.
- `ThumbnailPanel.VisibleRange`: sichtbare Miniaturen oben, in der Mitte, am Ende begrenzt,
  ohne Seiten leer.
- `ThumbnailPanel.PrefetchOrder` (seit 0.12.3): sichtbare, dann 12 in Scrollrichtung, dann 4 dagegen, an den Enden
  begrenzt; `MainWindow.Interleave`: Miniatur direkt hinter ihrer Hauptseite, übrige danach.
- Seitenvorrat (`PdfDocument.KeepPagesOpen`, seit 0.12.3): keine Doppelten, begrenzt, im Leerlauf freigegeben, und nach
  Verschieben, Löschen und Einfügen steht an vorher offenen Indizes die richtige Seite, Drehen leert ihn (Gegenprobe:
  ohne `ClosePages` in `MovePages` schlägt „Seitenvorrat nach Verschieben“ an).
- `MainWindow.NeighboursFor`: zwei Nachbarseiten je Richtung bei kleinen Seitenbildern, eine über
  8 MP, bei Doppelseite immer zwei (eine ganze Nachbarzeile).
- `Updater.Newest` (Liste von `/releases`, seit 0.13.1): ohne Vorabversionen die Freigabe, mit die neuere Vorabversion
  („-pre“ abgetrennt, PreRelease gesetzt), Entwürfe nie, kaputte Liste ergibt nichts.
- `Updater.Parse` (Antwort der GitHub-API): Tag und Setup unter mehreren Anhängen, ohne
  `sha256:`-Prüfsumme kein Update, kaputte Antwort und Fehlermeldung der API ergeben null;
  `Updater.Current` ist dreistellig (sonst wäre 0.10.1 kleiner als 0.10.1.0).
- Seiten bearbeiten (`EditChecks`, 4-Seiten-PDF mit Breiten 100–400 und Text je Seite): Löschen,
  Verschieben (an den Anfang, in eine Lücke weiter hinten, an dieselbe Stelle ohne Änderung), Einfügen
  einer anderen PDF, jeweils mit `Remap` der Drehungen; Drehen tauscht die Kanten; Speichern behält
  Reihenfolge, Drehung und Gliederung, und der entpackte Inhalt enthält die gelöschte Seite nicht mehr
  (PDFium packt beim Speichern mit FlateDecode); „In neue PDF“ mit Drehung wie in der Ansicht, ohne Seiten
  abgelehnt, ersetzt eine vorhandene Datei ohne Temp-Rest; kaputte Datei beim Einfügen und ungültiges
  Verschieben melden ihren Fehlercode; ein Lesezeichen rückt nach, wenn eine Seite davor fällt, und hat
  kein Ziel mehr (−1), wenn seine Seite gelöscht wird.
- Gliederung (`Outline`): das Test-PDF hat genau ein Lesezeichen „Kapitel Zwei“ auf Seite 2.
- Suche und Links (seit Stufe 2, `SearchAndLinkChecks`): `MainWindow.FindAll` ohne Groß/klein, auch Umlaute, nicht
  überlappend, leerer Begriff findet nichts; `TextRects` legt „Fletta Test“ oben links auf Seite 1 und auf der mit
  `/Rotate 90` gedrehten Seite 2 oben rechts (Anteile der Seite, `FPDF_PageToDevice` rechnet `/Rotate` ein).
  `PageLayout.Turn`: oben links wird nach R oben rechts, zurückdrehen ergibt die Stelle. `Links`: Link-Annotation mit
  Adresse samt Lage, Link auf Seite 2, Adresse als reiner Text (`FPDFLink_LoadWebLinks`); nach Löschen der Zielseite
  fällt der Seitenlink weg (Ziel −1).
- Passwort (`PasswordChecks`): der Selbsttest verschlüsselt selbst (RC4 40 Bit, Revision 2, Passwort mit „ä“ in
  Latin-1); ohne und mit falschem Passwort `PasswordError`, mit richtigem (als UTF-8 übergeben) lesbar, **Speichern
  behält den Schutz**, `PageRenderer` öffnet mit Passwort.
- Hintergrundarbeit (`PageRenderer.Invoke(…, whenIdle: true)`, Text für die Suche): läuft erst nach einer später
  gestellten Aufgabe.
- Anmerkungen (seit Stufe 3, `AnnotationChecks`, Vier-Seiten-PDF): `AnnotationEdit.MapOf` bildet oben links der Ansicht
  auf (0, 500) ab, rechts x+, unten y−; Textmarker (`CharAt` unter der Maus, `LineRects` eine Zeile, gelb im Bild),
  Freihand rot im Bild, Notiz samt Text und Ändern, Text-Stempel mit `/Contents`, waagrecht, dunkel im Bild, Bild-Stempel
  rot im Kasten; Speichern behält alle, Löschen nimmt eine weg; Freihand hat `/AP` auch ohne vorheriges Rendern
  (`WithNewAnnotation` rendert 1 × 1); Text auf einer `/Rotate-90`-Seite waagrecht, in gedrehter Ansicht senkrecht und
  unten links; Leerzeile im Stempeltext ist nur Abstand; `MissingInStampFont` meldet Zeichen außerhalb von WinAnsi
  (PDFium setzte sonst „ÿ“); Unterschrift: `Signatures.Normalize` (Rahmen 0–1, Seitenverhältnis) und `Clean` (weiß
  durchsichtig, zugeschnitten).
- Formulare (seit Stufe 4, `FormChecks`, eigene PDF 400 × 400 mit Textfeld „Name“, Kästchen „Ja“ samt Erscheinung
  „Yes“/„Off“, Auswahlfeld „Farbe“ Rot/Grün/Blau): `FormType` 1 und drei Felder mit Art, Name, Wert, Möglichkeiten;
  hellblaue Hervorhebung nur mit `forScreen` (Drucken und Kopieren ohne); `SetFieldText`, `ClickField` (Fokus +
  Leertaste), `SetFieldChoice` übernehmen, Text und Haken im Bild, leerer Text leert das Feld, Speichern behält Werte
  samt Umlauten.
- Datei von außen geändert (seit 0.14.2, `MainWindow.ChangedSince`/`StampOf`): unveränderte Datei gibt Speichern frei, nach
  dem Überschreiben mit anderem Inhalt ist es blockiert, ebenso bei gleicher Länge und neuer Zeit; eine gerade nicht lesbare Datei blockiert nicht. Den Dialog
  „… wurde außerhalb geändert – erst neu laden“ selbst zeigt nur das laufende Programm.
- Ziehordner (seit 0.14.2, `PageDragData.OwnerRuns`): ein Ordner „Prozess-ID-guid“ des laufenden Prozesses gilt als in
  Benutzung, ein alter Name ohne Prozess-ID und ein Ordner mit Prozess-ID 4 (System, kein Fletta) nicht.
- Seitentext (`PageText`, das Test-PDF trägt „Fletta Test“ in Helvetica).
- Text markieren (seit 0.14.0, in `AnnotationChecks` bei den Textmarker-Prüfungen): `MainWindow.CharUnder` findet das
  Zeichen unter dem Druck und liefert neben dem Text −1 (davon hängt ab, ob der Zug markiert oder die Ansicht schiebt);
  `PdfDocument.CharText` gibt zu den markierten Zeichen „Seite Eins“ zurück (Nummern der Zeichenliste, nicht des Textes)
  und begrenzt Nummern außerhalb der Seite auf die ganze Seite.
- Druck (`PrintJob`, der Weg hinter `Strg+P`): Drehung der Seite auf das Blatt bei hoch und quer,
  markierte Miniaturen als zusammenhängende Läufe (`RunsOf`), Skalierung (`ScaleFor`, seit 0.15.0):
  eingepasst wächst die Seite, 100 % bleibt 1:1, 200 % doppelt auch über das Blatt hinaus; `ParsePercent` liest „150“, „87,5 %“,
  „87.5“ und lehnt 0, Text und leer ab.
  `PdfDocument.Render` mit Lage über den Rand (`placement`, Ecke im Minus) zeigt den Ausschnitt ohne Verschieben, mit voller
  Lage pixelgleich zum Aufruf ohne. Die Option „Skalierung“ im Dialog, das Neuzeichnen der Vorschau beim Umschalten und die
  Vorschau mit Druckerrand prüft nur die Sichtprüfung (Wiki, Schritt 5, **noch nicht belegt**). Der Dialog selbst braucht ein
  Fenster und ist hier nicht prüfbar, siehe Sichtprüfung.
- `PageRenderer.Invoke`: Aufgabe auf dem Render-Thread (Seitentext), nach dem Ende abgebrochen.
- Echter Druck über den Spooler auf „Microsoft Print to PDF“ in eine Datei (zwei Seiten, zweite
  gedreht); fertig erst, wenn der Spooler sie freigegeben hat und sie mit `%PDF` beginnt und mit
  `%%EOF` endet. Ohne diesen Drucker steht dort `skip`, das ist kein Fehler. Bleibt der
  Temp-Ordner liegen, steht dort `warn` — auch kein Fehler.
  Das ist der **Altweg** (`PagePrinter`, PDFium direkt auf den Druckerkontext): seit 0.10.0 druckt
  Fletta über `SystemPrint`, `PagePrinter` steht nur noch für diese Prüfung ohne Fenster.
- Einstellungen: Hin und zurück durch JSON, Zoommodus als Text, kaputte Datei ergibt Standardwerte, Standardwerte mit
  Fensterlage NaN lassen sich speichern (seit 0.13.1; warf vorher und brachte den Updates-Dialog zum Absturz),
  Fenster außerhalb des Bildschirms oder nie gespeichert wird verworfen (mittig).
- Anmeldung als PDF-Programm (`FileAssociation.Entries`): Öffnen-Befehl mit Anführungszeichen
  bei Pfad mit Leerzeichen, `.pdf` zeigt auf die ProgID.
- `PageRenderer` end-to-end: Fehlercode einer kaputten Datei über `Opened`, Seitengrößen,
  dass ein bestelltes Bild über den Dispatcher zugestellt wird (Pixelprobe), und dass `Closed`
  nach `Dispose` bzw. nach dem Fehlschlag erfüllt wird. Der Selbsttest wartet darauf, bevor er
  den nächsten Renderer startet und den Temp-Ordner löscht.

Nicht im Selbsttest: der Wächter auf die geöffnete Datei (`FileSystemWatcher`, Hinweis „… wurde geändert · Neu laden“)
und alles, was an der Maus hängt — beides steht in der Sichtprüfung (Wiki, „Prüfweg (privat)“, Schritt 11).

`--measure` schließt das Fenster auch bei fehlender oder kaputter Datei und ohne Datei;
`measure.ps1 -Pdf C:\gibt-es-nicht.pdf -Runs 1` muss mit einer `measure:`-Zeile zurückkommen.

Nicht abgedeckt und nur am laufenden Programm prüfbar: `Strg+P` samt Vorschau, Fensteraufbau, Werkzeugleiste und
Bodenleisten (Zoommenü, Seitenfeld), Miniaturen-Menü, Ziehen und Ablegen von Seiten, Textauswahl mit der Maus samt I-Balken, Schieben mit der mittleren
Taste, Hinweis „… wurde geändert · Neu laden“, Speichern-
Rückfrage (`AskDialog`), Speichern-Knopf, Mausrad über den Miniaturen, Scrollen und das Nachladen der Seiten, Zoom mit Tasten und
Strg+Rad, Halten der Lesestelle beim Zoomen, `R`/`L` samt sofortigem Vorab-Drehen des alten
Bildes, Ziehen und Ablegen, Ziehen zum Scrollen (linke Maustaste **neben** dem Text, über Text markiert sie seit
0.14.0; die mittlere Taste schiebt überall), `Strg+O`, `Esc`, `Strg+W` (seit 0.14.1). Seit Stufe 2 dazu:
Suchleiste (`Strg+F`, Enter/F3 samt Umschalt, `Esc`, Marken, Lesen im Hintergrund), Passwortdialog (falsches
Passwort, Abbrechen), Link-Klick samt Sperre für andere Schemata als http/https/mailto und Tooltip. Seit Stufe 3:
Anmerkungswerkzeuge in der Werkzeugleiste und ihre Mauszüge samt Skizze, Rechtsklick-Menü auf Anmerkungen, Notiz- und
Text-Dialog (Größenwahl), Unterschrift zeichnen (`InkCanvas`) und aus Bild laden, Tastenriegel während eines Zugs.
Seit Stufe 4: Klick in Formularfelder, Eingabefeld darüber (Enter, Tab zum nächsten Textfeld, Esc), Auswahlmenü, das
beim Loslassen aufgeht, Hinweis bei XFA-Formularen.

## Sichtprüfung

Alles mit Fenster (Druckdialog, Maus, Ziehen und Ablegen, Update von Hand, Änderungsmodus, Stufen 2–5) prüft eine
Test-VM; Ablauf, Schritte und Werkzeug-Fallen stehen auf der Wiki-Seite `fletta` unter „Prüfweg (privat)“.

Live-Schritt Datei von außen geändert und Deinstallation (seit 0.14.2, **noch nicht belegt**), je in einer Wegwerf-Kopie:

- PDF öffnen, eine Seite löschen („●“), die Datei von außen ersetzen (andere Länge oder Zeit), dann Strg+S: Dialog
  „… wurde außerhalb geändert – erst neu laden“ mit „Abbrechen“ im Fokus (Enter verwirft nichts) und dem Hinweis
  „In neue PDF kopieren …“. „Abbrechen“: Datei unverändert, „●“ bleibt, das Fenster lässt sich danach schließen.
- Speichern mit gesperrter Zieldatei (von außen exklusiv offen halten) und währenddessen ändern: der Move-Fehlerpfad
  fragt, „Abbrechen“ schließt das Dokument und nennt den Pfad der Temp-Datei; „Neu laden“ verwirft die Schritte, die
  Temp-Datei bleibt und ihr Pfad steht im Dialog. In beiden Fällen lässt sich das Fenster danach schließen.
- Zwei Schritte (zwei Seiten löschen), Datei von außen ersetzen, Strg+Z, „Abbrechen“: beide Schritte bleiben, Strg+Z
  nach dem Neuladen zählt wieder richtig.
- Deinstallieren (`unins000.exe`), Rückfrage „Wirklich entfernen?“ erst mit „Nein“: nichts passiert, Fletta läuft weiter.
  Dann mit „Ja“ in drei Fällen: Fletta läuft nicht (entfernt ohne weitere Frage); Fenster ohne Änderungen (schließt sich,
  entfernt); Fenster mit Änderungen (fragt „Speichern?“; nach 10 s ohne Antwort kommt „Fletta läuft noch … Wiederholen,
  oder Abbrechen“, „Abbrechen“ lässt alles installiert, „Wiederholen“ nach dem Schließen entfernt).

## Messen (von Hand, öffnet kurz Fenster)

```powershell
tools\measure.ps1 -Pdf '<pfad>' [-Runs 5]
```

Veröffentlicht mit ReadyToRun und startet `Fletta.exe --measure` je Lauf. Jede Zeile nennt die
Zeit seit Prozessstart für jede Marke und die Seitenzahl. Die Marken stehen in der Reihenfolge,
in der sie eintreten; `fenster` und `dokument` laufen parallel und können die Plätze tauschen:
`startup` (OnStartup), `fenster` (leeres Fenster gezeichnet), `dokument` (Seitengrößen da),
`leiste` (Miniaturen befüllt), `updateview` (erstes Setzen der sichtbaren Seiten),
`auftrag seite`/`gerendert seite` (Render-Thread, erste Hauptseite), `seite` (Bild im UI-Thread
angekommen), `erstes bild` (danach gezeichnet); `… miniatur` und `aufgabe` kommen danach.

Zielwert: erstes Bild um 0,6 s mit einem 195-MB-Handbuch (990 Seiten), warm.
Liegt `updateview` weit hinter `leiste`, blockiert ein Layout-Durchgang den UI-Thread — so fiel
am 2026-09-10 die Seitenleiste auf, die mit 0 hohen Einträgen alle 990 aufbaute (1,5 s statt 0,6 s).

## Version — eine Stelle

| Datei | Symbol |
|---|---|
| `src/Fletta.csproj` | `<Version>` |

Die Release-CI liest sie von dort; ein Tag `vX.Y.Z` muss genau dazu passen, sonst bricht sie ab.
Zur neuen Version gehört ein Abschnitt `## X.Y.Z` in `CHANGELOG.md` — daraus kommt der Text des Releases,
ohne ihn bricht ein Tag-Lauf im Schritt „Version“ ab (von Hand gestartete Läufe nicht).

## PDFium

`PdfiumTag` und `PdfiumSha256` in `src/Fletta.csproj` ziehen **immer zusammen** um. Der Build
lädt `pdfium-win-x64.tgz` von `bblanchon/pdfium-binaries` nach `lib/pdfium-<build>/` und bricht
bei abweichendem SHA-256 ab, ohne zu entpacken. Den Hash liefert
`gh api repos/bblanchon/pdfium-binaries/releases/latest` im Feld `digest` des Assets.

Die Signaturen in `src/Pdfium/Native.cs` stammen aus den Headern genau dieses Tags: `fpdfview.h`, `fpdf_doc.h`
(Lesezeichen, Links), `fpdf_text.h` (Text, Trefferlage, Adressen im Text), `fpdf_searchex.h`, `fpdf_edit.h`,
`fpdf_ppo.h`, `fpdf_save.h`, `fpdf_signature.h`, `fpdf_annot.h` (Anmerkungen, alles „experimental“),
`fpdf_formfill.h` (Formulare; `FPDF_FORMFILLINFO` Version 1 — das Layout in `Native.FormFillInfo` muss zum Header passen). Wer den Tag hochzieht, gleicht sie gegen die Header im Archiv
(`include/`) ab.

## Konventionen (gegen geänderte Dateien prüfen)

- Bezeichner im Code englisch, Text und Kommentare deutsch mit echten Umlauten (ä/ö/ü/ß).
  Ausnahme: `tools/*.ps1` — Windows PowerShell 5.1 liest UTF-8 ohne BOM als ANSI, dort
  deshalb ae/oe/ue.
- Formularumgebung (`PdfDocument.form`): jede Seite, die `FORM_OnAfterLoadPage` oder `FPDF_FFLDraw` gesehen hat,
  geht vor `FPDF_ClosePage` durch `FORM_OnBeforeClosePage` (`PdfDocument.ClosePage`), sonst zeigt eine Seitenansicht
  der Formularumgebung ins Leere. Seiten nur über `LoadPage`/`ClosePage` öffnen und schließen.
- PDFium ist nicht threadsicher: im Programm ruft nur der Thread von `PageRenderer` es auf,
  nie der UI-Thread. Nacheinander verschiedene Threads sind in Ordnung (neues Dokument im
  leeren Fenster), gleichzeitig zwei nie.
- Drehen mit R/L verändert die Datei nie; in die Datei schreibt nur Strg+S (Änderungsmodus) oder
  „In neue PDF …“ und Ziehen von Miniaturen (neue Datei). Speichern schreibt immer erst vollständig in
  eine Temp-Datei daneben und ersetzt dann (`PdfDocument.Save`), nie inkrementell.
- Wer die Datei schreibt oder die gesammelten Schritte neu anwendet, prüft vorher `loadedStamp` gegen den Stand auf der
  Platte: Speichern über `BlockedByOutsideChange`, Wiederanwenden zentral in `Reload` (seit 0.14.2).
- Jedes klickbare Element tut etwas Echtes; alles, was Daten zeigt, hat Leer-, Lade- und
  Fehlerzustand.

## Release und Installer (GitHub Actions)

`.github/workflows/release.yml` läuft auf `windows-2025` (bringt .NET-10-SDK und Inno Setup 6.7
mit) bei einem Tag `v*` oder von Hand: self-contained Publish nach `publish/`, `--selftest` gegen
genau diesen Publish, dann `ISCC /DAppVersion=… installer\fletta.iss` → `dist/Fletta-X.Y.Z-setup.exe`,
bei einem Tag als Release-Anhang. Lokal gibt es kein Inno Setup; die `.iss` wird nur dort gebaut.
Release-Text: Abschnitt `## X.Y.Z` aus `CHANGELOG.md` (`--notes-file`), gilt auch fürs Prerelease.
Ein Tag `vX.Y.Z-pre` (gleiche `<Version>`) legt ein **Prerelease** an: GitHub liefert es nicht unter
`releases/latest`; der Update-Hinweis bietet es nur an, wer unter „Updates“ Vorabversionen eingeschaltet hat (seit 0.13.1
fragt Fletta `/releases` ab). Eine Vorabversion hat dieselbe `<Version>` wie ihre Freigabe; wer sie installiert hat,
bekommt die Freigabe nicht noch einmal angeboten — gleicher Commit. Neue Korrekturen brauchen deshalb eine neue Nummer.
Alte Vorabversionen löschen, sobald ihre Freigabe oder die nächste da ist (`gh release delete vX.Y.Z-pre --cleanup-tag`).
Freigeben heißt danach `vX.Y.Z` auf
denselben Commit setzen — die CI baut dann das normale Release. Nachweis nach dem Lauf:
`gh release list` im Checkout zeigt das Prerelease als „Pre-release“, „Latest“ bleibt beim
vorigen Release.
Das Setup installiert pro Benutzer nach `%LOCALAPPDATA%\Programs\Fletta` und ruft `--register`
bzw. beim Entfernen `--unregister` auf. Offene Fenster beendet das Entfernen erst ohne `/F` (ungespeicherte Änderungen
fragen nach); läuft Fletta nach etwa 10 s noch, fragt es „Wiederholen/Abbrechen“, `/F` nur beim stillen Entfernen
(`CurUninstallStepChanged(usAppMutexCheck)`, nach der Rückfrage „Wirklich entfernen?“, nur Prozesse
des eigenen Benutzers; seit 0.14.2). Der Job hat `timeout-minutes: 20`. Geprüft wird es auf der Test-VM mit dem Setup aus dem
Artefakt des Laufs.

## Was hier NICHT geprüft wird

- Kein Deploy. Installiert wird über das Setup aus der CI oder zum Entwickeln mit `tools/install.ps1` (Publish nach
  `%LOCALAPPDATA%\Programs\Fletta`, `--register`, Standard-Apps öffnen) — auf dem Entwickler-PC nur nach Rückfrage;
  die Test-VM prüft `--register`/`--unregister` in ihrer eigenen HKCU.
- Gemerkt wird unter `%APPDATA%\Fletta\settings.json`, die Unterschrift unter `unterschrift.json` (gezeichnet) und
  `unterschrift.png` (aus Bild) daneben; `--measure` speichert nichts.
