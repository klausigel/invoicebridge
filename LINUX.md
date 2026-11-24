# InvoiceBridge-CLI unter Windows und Linux

Unterstützt wird der bekannte Musterinhalt 112684 mit dem bisherigen
ZUGFeRD-Profil EN 16931 und PDF/A-3b. Derselbe verwaltete .NET-Build wird
auf beiden Plattformen ausgeführt. Der Containerlauf erhält ihn ohne
Quellkopie und ohne nachträgliche Änderungen.

## Build und Aufruf

Mit .NET SDK 10.0.403 aus dem Projektordner bauen. Bei bereits vorhandenen
Restore-Artefakten kann der erste Befehl entfallen.

```text
dotnet restore Kig.InvoiceBridge.csproj --locked-mode
dotnet publish Kig.InvoiceBridge.csproj --no-restore -c Release -o artifacts/cross-platform-publish
```

Unter Windows bleibt der r4-Paketaufbau mit `runtime/`, `tools/` und
`invoicebridge.json` erhalten. Standardaufruf im Paketordner:

```powershell
.\invoicebridge.exe 112684
```

Unter Linux das veröffentlichte DLL-Verzeichnis und die Konfiguration lokal
bereitstellen. `invoicebridge.example.json` ist die Vorlage. Für den Lauf
als `invoicebridge.json` speichern und die bestätigten Stammdaten eintragen.
`ToolsDirectory` enthält `Mustang-CLI-2.26.0.jar` und
`verapdf/bin/cli-1.30.3.jar`. Die Ubuntu-Werkzeuge liegen unter `/usr/bin/java`,
`/usr/bin/gs` und `/usr/share/color/icc/ghostscript/default_rgb.icc`. Optional
überschreibt `GhostscriptExecutable` den Ghostscript-Pfad. Relative Einträge
in der JSON gelten ab dem Ordner der Datei. Standardmäßig sucht die CLI
`invoicebridge.json` neben der Anwendung.

```sh
dotnet /path/to/publish/invoicebridge.dll 112684 --config /path/to/invoicebridge.json
```

Geprüfte Versionen: .NET 10.0.14, Mustang 2.26.0, veraPDF 1.30.3, PdfPig 0.1.15,
ZUGFeRD-csharp 18.0.1 und ZXing.Net 0.16.11. Windows verwendet die r4-Werkzeuge
Ghostscript 10.08.0 und Temurin 21.0.13+9. Linux verwendet Ubuntu 24.04 mit
Ghostscript 10.02.1 (`10.02.1~dfsg1-0ubuntu7.10`) und OpenJDK 21.0.13.1
(`21.0.13.1+2-1~24.04.1`). Der Werkzeug-Dockerfile fixiert das .NET-Basisimage
nach Digest und die beiden Ubuntu-Werkzeugpakete nach Version. Ein Neubau
benötigt die Paketquellen mit diesen Versionen.

## Regression im lokalen Workspace

Vorausgesetzt werden Python 3, die bereitgestellte Musterrechnung in
`../examples`, das extrahierte XML der r4-Referenz unter `artifacts`, die
vorhandenen Validator-JARs unter `.tools` und die vorhandene entpackte
Windows-r4-Paketstruktur unter `artifacts/Kig.InvoiceBridge-20261102-r4-win-x64`.
Alle Rechnungs- und Referenzdateien bleiben lokal und außerhalb der Versionsverwaltung.
Abweichende lokale Pfade über `--sample`, `--reference`, `--config` und
`--tools` angeben. `--tools` zeigt unter Windows auf einen Ordner mit dem
bisherigen Paketlayout, unter Linux auf den Ordner mit Validator-JARs.

Aus dem Projektordner unter Windows:

```powershell
Copy-Item artifacts/Kig.InvoiceBridge-20261102-r4-win-x64/runtime artifacts/cross-platform-publish/runtime -Recurse
py -3 scripts/test-platforms.py --platform windows
py -3 scripts/test-platforms.py --platform linux
```

Die Laufzeit einmalig in das frische Publish-Verzeichnis kopieren. Statt
`py -3` kann ein vorhandener Python-3-Interpreter direkt aufgerufen werden.
Der Linux-Test verwendet den Docker-Kontext `desktop-linux` und standardmäßig
die genaue lokale Image-ID des bestandenen Tests. Wenn das Image fehlt:

```powershell
docker --context desktop-linux build -t invoicebridge-linux-regression:10.0.403 scripts/linux
py -3 scripts/test-platforms.py --platform linux --image invoicebridge-linux-regression:10.0.403
```

Das Image enthält nur Werkzeuge. Der Linux-Runner teilt Rechnungsdatei,
Referenz, Konfiguration und verwaltete Build-Artefakte erst zur Laufzeit über
den lokalen Docker-Socket in ein RAM-Dateisystem. Der Container hat kein
Netzwerk, einen schreibgeschützten Image-Unterbau und deaktivierte Docker-Logs.
Der Build-Kontext enthält keine Rechnung und keine Konfiguration.

Jeder Konvertierungsauftrag hat seinen eigenen temporären Ordner im
Zielverzeichnis. Die CLI schreibt keine Rohberichte der Werkzeuge und keine
Rechnungswerte in ihre Statusmeldungen. Die Regression prüft die Bereinigung
vor der Test- und Containerbereinigung. Im Fehlerlauf wird nach bestandener
XML-Prüfung der per Konfiguration ausgewählte Konverter durch einen Prozess
mit Fehlerexit ersetzt. Der Test erwartet Exitcode 21 und einen leeren Zielordner.
Ein abrupter Abbruch durch das Betriebssystem ist separat zu behandeln.

## Lokales Windows-ZIP

Mit den bereits bereitgestellten Werkzeug- und Quellenarchiven in `.tools`
und einem Windows-Publish mit AppHost erzeugt der folgende Aufruf ein Paket
mit Vorlage, Lizenzhinweisen und Quellen:

```powershell
py -3 scripts/package.py --version local-review --publish-dir artifacts/cross-platform-publish
```

Die echte `invoicebridge.json`, Rechnungs-PDFs und Referenz-XML bzw.
Snapshots sind standardmäßig ausgeschlossen. Für einen ausdrücklich privaten
Test kann `--include-local-sample <PDF>` die bekannte Musterdatei beilegen.
Die CLI-Konfiguration bleibt auch dann separat. Dieses ZIP trägt den
Namenszusatz `-private-sample`.

## Letzte technische Abnahme

Der Test extrahiert aus der veröffentlichten PDF erneut die XML und vergleicht
den XML-Baum vollständig mit r4, einschließlich Beträgen, Steuer und
Zahlungsdaten. Zusätzlich prüft er die veröffentlichte PDF nochmals mit
veraPDF. Die CLI prüft XML-Schema, Mustang-Geschäftsregeln, Hybridstruktur,
PDF/A, Rückextraktion, GiroCode und die gerenderte Seite vor der Veröffentlichung.
Die PDF-Dateien dürfen sich binär unterscheiden. Der Test löscht auch die
Testausgaben und gibt ausschließlich Prüfstatus und Zeiten aus.

| Abnahmelauf | Konvertierung mit integrierten Prüfungen und Cleanup | Gesamte Regression |
| --- | ---: | ---: |
| Windows, Standardaufruf der EXE | 17,464 s | 27,547 s |
| Linux, lokaler Docker-Container | 11,501 s | 22,641 s einschließlich Containerstart, Übergabe und Entfernung (18,068 s im Container) |

Beide Läufe bestanden einschließlich Cleanup nach Erfolg und Werkzeugfehler.
Die zusätzliche Prüfung der finalen PDF ergab 146 bestandene veraPDF-Regeln
je Plattform mit 13.404 Prüfungen unter Windows und 13.389 unter Linux.
Build und Imageerstellung sind außerhalb der Messungen. Die Werte sind
Einzelmessungen und kein Plattformbenchmark.
