# InvoiceBridge: Start und Konfiguration

Lokaler Prototyp für **genau die freigegebene Musterrechnung 112684**.
Nicht als allgemeine Rechnungsverarbeitung oder Produktionsfreigabe verwenden.

1. ZIP vollständig in einen beschreibbaren Ordner entpacken.
2. `invoicebridge.example.json` als `invoicebridge.json` neben die EXE kopieren.
   Dort die bestätigten Bankdaten eintragen und Quell- und Zielordner einstellen.
   Die freigegebene Muster-PDF separat im Quellordner bereitstellen.
3. Im Paketordner starten:

```powershell
.\invoicebridge.exe 112684
```

Ergebnis: `output\Rechnung_112684_ZUGFeRD.pdf`. Vorhandene PDFs werden nicht
überschrieben. Statusmeldungen enthalten keine Rechnungsdaten.
Bei einem wiederholten Test ein anderes Zielverzeichnis in einer separaten
Konfigurationsdatei angeben:

```powershell
.\invoicebridge.exe 112684 --config C:\MeinTest\invoicebridge.json
```

## Konfigurationsregeln

- Standarddatei: `invoicebridge.json` direkt neben `invoicebridge.exe`.
- Nur `--config` überschreibt diese Wahl. Ein relativer `--config`-Pfad gilt ab
  aktuellem Arbeitsverzeichnis; das Arbeitsverzeichnis wird sonst nicht als
  Konfigurationsquelle verwendet.
- Sämtliche relativen Verzeichniswerte in JSON gelten ab **Ordner der gewählten
  Konfigurationsdatei**. Absolute Pfade sind erlaubt. Keine ENV-Variablenersetzung.
- `SourceFileSelection` muss `InvoiceNumberToken` sein. Direkt im Quellordner
  werden Dateien mit der Endung `.pdf` (Groß-/Kleinschreibung egal) gesucht,
  ohne Unterordner. Die vollständige angegebene Nummer muss als Ziffernfolge
  im Dateinamen ohne Erweiterung stehen: `Kunde_112684.pdf`, `112684.PDF`
  und `AndererKunde112684_export.pdf` passen; `1112684.pdf` und `1126840.pdf`
  passen nicht. Auch angrenzende Unicode-Ziffern verhindern einen Treffer.
  Präfix und Suffix sind frei; führende Nullen werden nicht entfernt.
- Kein Treffer oder fehlender Quellordner: Exitcode 10. Mehrere Treffer:
  Exitcode 13 mit Liste aller passenden Pfade; keine Auswahl nach Datum,
  Inhalt oder alphabetischer Reihenfolge. Erst bei genau einem Treffer wird
  der unveränderte freigegebene PDF-Inhalt geprüft. Andere Inhalte bleiben
  ausgeschlossen, auch wenn ihre Dateinamen passen.
- Migration von r3: `SourceFilePattern` aus eigener JSON entfernen und durch
  `"SourceFileSelection": "InvoiceNumberToken"` ersetzen. Die alte Einstellung
  wird als unbekanntes Feld mit Konfigurationsfehler abgewiesen, nicht ignoriert.
- Die Ausgabe heißt fest `Rechnung_<Nummer>_ZUGFeRD.pdf`.
- `ToolsDirectory` zeigt auf die mitgelieferten Werkzeuge, standardmäßig `tools`.
  Bei einer Konfigurationsdatei außerhalb des Pakets diesen Wert als absoluten
  Pfad zum Paket-`tools`-Ordner setzen.
- `Bank` enthält IBAN, Kontoinhaber, BIC und Bankname. IBAN-Leerzeichen werden
  entfernt; IBAN/BIC in Großschreibung normalisiert. Prüfsumme und Kontoinhaber
  werden geprüft. Für diesen engen POC sind ausschließlich die bereits
  bestätigten NetProof-Bankdaten erlaubt; andere Werte werden abgewiesen, damit
  sie nicht dem unveränderten grafischen Briefkopf widersprechen.
- Rechnungsnummern sind 1 bis 20 Ziffern. Eine fehlende Quelldatei ergibt 10;
  eine vorhandene abweichende Datei oder falsche innere Rechnungsnummer ergibt 11.
- Ein `.lock` verhindert gleichzeitige Bearbeitung desselben Ausgabeziels.

`global.json` im beigefügten Quelltext betrifft nur die SDK-Auswahl beim Bauen.
Es gibt keine Datenbank-/Access-Anbindung und keinen Internetabruf von Rechnungen.

## Exitcodes

| Code | Bedeutung |
| --- | --- |
| 0 | Alle automatischen Prüfungen bestanden; neue PDF erstellt |
| 2 | Aufruf oder Konfiguration ungültig/fehlend |
| 10 | Rechnungsdatei nicht gefunden |
| 11 | Muster oder Rechnungsnummer nicht unterstützt |
| 12 | Ausgabe oder Ausgabesperre bereits vorhanden |
| 13 | Mehrere passende Quell-PDFs; keine Auswahl vorgenommen |
| 20 | Benötigte mitgelieferte Abhängigkeit fehlt |
| 21 | Schema-, Geschäftsregel-, PDF/A-, Hybrid-, XML-, GiroCode- oder Bildprüfung gescheitert |
| 30 | Datei-/Berechtigungsfehler |

Fehler gehen mit Code und einer bereinigten Meldung an stderr. Jeder Auftrag
verarbeitet seine Zwischenstände in einem eigenen `.invoicebridge-*`-Ordner
unter dem Zielverzeichnis. Nach Erfolg oder behandeltem Fehler räumt die CLI
diesen Ordner auf. Die finale PDF wird erst nach den Prüfungen und nach dem
Aufräumen veröffentlicht. Ein harter Prozess- oder Systemabbruch kann
Arbeitsreste hinterlassen. Das Zielverzeichnis muss zugriffsgeschützt sein.

## Enthaltene Abhängigkeiten und Prüfungen

Windows x64, .NET 10.0.14 aus MIT-lizenzierten offiziellen NuGet-Komponenten,
Eclipse Temurin JRE 21.0.13+9, Ghostscript 10.08.0, Mustang 2.26.0, veraPDF 1.30.3
und die festgehaltenen .NET-Bibliotheken sind im Paket enthalten. Kein SDK,
globales Java, Python, Netzwerk oder Paketdownload ist für den CLI-Lauf nötig.
Es werden ausschließlich die mitgelieferten Laufzeiten gesucht.

Das CLI führt weiterhin getrennte lokale XSD-, XML-Geschäftsregel-, Hybrid- und
PDF/A-3b-Prüfungen sowie XML-Rückextraktion/Inhaltsabgleich durch. Zusätzlich
rendert es Original und Ergebnis mit dem gebündelten Ghostscript, dekodiert
den **gerenderten** GiroCode und vergleicht dessen Zahlungsdaten. Ein begrenzter
Bildvergleich prüft außerhalb des bewusst ergänzten GiroCode-Bereichs. Die
Rasterdateien sind temporäre P6-PPM-Dateien (8-Bit RGB, 150 dpi).
Die Prüfberichte aus der r4-Paketserie beruhen auf dem damaligen
`.evidence`-Format. Aktuelle Läufe geben bereinigte Statusmeldungen aus.
Für beide Plattformen siehe [Linux und Regressionstests](LINUX.md).

Es wird keine Laufzeitoptimierung behauptet. Alle Prüfer bleiben aktiviert;
insbesondere das bisherige Zehn-Sekunden-Ziel war nicht erfüllt.

## Rechte und Grenzen

Eigener Quelltext unter AGPL-3.0-or-later in `source`, Lizenz in `LICENSE`.
Laufzeiten und Prüfer enthalten weitere Lizenzen: siehe `licenses`, Java-`legal`
und `NOTICE`, sowie `THIRD-PARTY-NOTICES.md`. Passende Quellen für Ghostscript,
Temurin und die betroffenen Prüfbibliotheken liegen unter `sources`.
Keine Oracle-Java-Installation und kein Microsoft-Runtime-ZIP mit zusätzlichen
Weitergabeklauseln wurden in dieses Paket kopiert.

Die .NET-NuGet-Laufzeit wurde aus den ausdrücklich MIT-lizenzierten Runtime-
und Host-Komponenten zusammengesetzt. Alle Originalhinweise und NuGet-
Lizenzmetadaten liegen bei. Das ist ein lokal prüfbares Paket; eine Übergabe
an Manuel oder Dritte ist nicht erfolgt. Windows-Systemkomponenten bleiben
Voraussetzung. Der Test außerhalb des Checkouts auf demselben Entwicklungs-PC
ist kein Test auf einem vollständig sauberen Fremdrechner.

Zum Wiederbauen benötigt man das SDK gemäß `source/global.json`. Die drei
direkten Bibliothekspakete sind unter `source/packages` beigelegt; Restore mit
`dotnet restore --configfile NuGet.config --source packages --locked-mode`.
Ein SDK ist nur für Änderungen/Builds erforderlich, nicht zur Ausführung.
