# Lokaler InvoiceBridge-HTTP-Test

Der ASP.NET-Core-Dienst verarbeitet die unveränderte NetProof-Musterrechnung
112684 mit demselben Konvertierungs- und Prüfablauf wie die CLI. Er akzeptiert
die zwei lokalen Testprofile KIG und Manuel. Die Ergebnisse sind Testdokumente
mit fiktiven Konten und der sichtbaren Kennzeichnung `TEST - NICHT ZAHLEN`.
Der HTTP-Pfad rastert die genehmigte Seite mit 150 dpi und entfernt den
ursprünglichen Bankbereich aus den Pixeln. Der Ersatz steht als Text auf der
Seite und die gleichen Daten stehen in XML und GiroCode. Der sonstige
Rechnungstext auf der sichtbaren Seite bleibt visuell erhalten und ist in dieser
Ausgabe nicht mehr als PDF-Text durchsuchbar. Beträge, Steuer und Parteien
werden vor dem Rastervorgang aus der ursprünglichen PDF gelesen.

## Build

Im Projektordner unter Windows mit .NET 10, Python 3 und Docker Desktop im
Linux-Kontext. Siehe [LINUX.md](LINUX.md) für den festgeschriebenen
Werkzeug-Dockerfile, .NET, Ghostscript, Java, Mustang und veraPDF.

```powershell
$python = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if (!(Test-Path -LiteralPath $python)) { $python = 'python' }
dotnet restore .\server\InvoiceBridge.Http.csproj --configfile .\NuGet.config
dotnet publish .\server\InvoiceBridge.Http.csproj --no-restore -c Release -p:UseAppHost=false -o .\artifacts\http-publish
```

Für vorhandene lokale NuGet-Pakete kann Restore zusätzlich
`--source .\.packages -p:NuGetAudit=false` verwenden. Der Launcher erwartet
das in [LINUX.md](LINUX.md) angegebene Linux-Werkzeug-Image. Alternativ das
Image aus `scripts/linux` bauen und seinen Namen mit `--image` mitgeben.
`--tools` ersetzt den Standardordner `.tools` für Mustang und veraPDF.

## Starten und aufrufen

```powershell
New-Item -ItemType Directory -Force .\artifacts\http-local | Out-Null
$profiles = '.\artifacts\http-local\tenants.local.json'
if (!(Test-Path -LiteralPath $profiles)) {
    & $python .\scripts\http-container.py profiles --profiles $profiles
}
& $python .\scripts\http-container.py serve --profiles $profiles
```

Der Generator erzeugt unterschiedliche zufällige Testschlüssel und zwei
fiktive deutsche Konten mit Prüfziffer und dem Platzhalter-Bankcode
`00000000`. Er überschreibt keine vorhandene Datei. Die Profile werden beim
Start aus einer lokalen Datei in ein RAM-Dateisystem des Containers eingelesen.
Der Server speichert die Schlüssel danach als SHA-256-Prüfwerte im Speicher.
Der Launcher entfernt die Profildatei im Container nach dem Start.
Der lokale Schlüsselbestand bleibt in der ignorierten Datei. Zur Rotation
neue Profile erzeugen und die Instanz neu starten.

Docker veröffentlicht Container-Port 8080 auf `127.0.0.1:8080` des Hosts.
Der lokale HTTP-Durchstich ist für den eigenen Rechner gedacht. Den Dienst
mit Strg+C beenden. Der Launcher entfernt dann den Container.

Ein `POST /v1/convert` erwartet die PDF-Bytes direkt im Body mit
`Content-Type: application/pdf` und den Schlüssel im Header `X-Api-Key`.
Der Body ist auf 1 MiB begrenzt. Zwei Konvertierungen können parallel laufen.

```powershell
curl.exe --fail-with-body http://127.0.0.1:8080/v1/convert -H "X-Api-Key: <TEST-API-KEY>" -H "Content-Type: application/pdf" --data-binary "@<PFAD-ZUR-MUSTERRECHNUNG>" -o "<ZIEL-PDF>"
```

`200` liefert ein geprüftes `application/pdf`.
Fehler liefern `application/problem+json` mit einem stabilen `code`.

| HTTP | Code | Bedeutung |
| --- | --- | --- |
| 401 | `api_key_required` | Schlüssel fehlt oder ist ungültig |
| 413 | `upload_too_large` | Upload größer als 1 MiB |
| 415 | `pdf_required` | Medientyp ist nicht `application/pdf` |
| 422 | `payment_conflict` | Lesbare Zahlungsdaten widersprechen dem Profil |
| 422 | `unsupported_pdf` | PDF entspricht nicht dem genehmigten Muster |
| 429 | `busy` | Beide Verarbeitungsslots belegt |
| 500 | `tool_failed`, `conversion_failed` | Werkzeugfehler oder fehlgeschlagene Verarbeitung/Validierung |
| 504 | `conversion_timeout` | Gesamtes Verarbeitungsbudget überschritten |

Die Routine prüft maschinenlesbare deutsche IBANs und beschriftete Angaben
wie IBAN, BIC, Kontoinhaber und Bankname, soweit sie sich aus der Textebene
lesen lassen. Der grafische Ersatz gilt ausschließlich für das exakt per
SHA-256 bekannte Muster. Es gibt keine OCR und keine Unterstützung anderer
Layouts.

Der Dienst legt pro Upload ein privates Arbeitsverzeichnis im tmpfs an.
Er entfernt es vor der PDF-Antwort und bei behandelten Fehlern einschließlich
abgebrochener Uploads. Die PDF-Antwort ist im Speicher auf 4 MiB begrenzt.
Der Container läuft mit schreibgeschütztem Root-Dateisystem und abgeschaltetem
Docker-Logtreiber. Logs und Fehler enthalten keine Nutzdaten oder Schlüssel.
Der Werkzeug-Image-Build enthält ausschließlich Werkzeuge.

## Abnahme nachstellen

```powershell
& $python .\scripts\http-container.py test --sample "<LOKALE-MUSTER-PDF>" --reference "<LOKALES-R4-XML>"
```

Der Test erzeugt Wegwerfschlüssel und die beiden Profile im Speicher,
isoliert die HTTP-Instanzen und entfernt sie am Ende. Er prüft jede Antwort
gegen die Windows-r4-Finanzwerte und führt für beide Einzelanfragen die
Mustang-Hybridprüfung und veraPDF erneut am Antwort-PDF aus. Der Dienst selbst prüft
zuvor XSD, Mustang-XML-Geschäftsregeln, PDF/A-3b, Hybridstruktur,
XML-Rückextraktion, GiroCode und den Bildvergleich. Der Test überprüft auch
die Mandantentrennung bei nachweislich überlappenden Uploads, Authentisierung,
Zahlungskonflikt, Abbruch und die Arbeitsverzeichnisse. Die Testantworten
werden nicht dauerhaft gespeichert.

Der finale lokale Abnahmelauf dauerte 56,250 Sekunden und bestand vollständig.
KIG und Manuel erhielten einzeln und parallel HTTP 200 mit der jeweils eigenen
fiktiven Bankverbindung. Die unabhängigen Prüfungen beider Einzelantworten
meldeten Mustang und PDF/A-3b gültig. Fehlende und ungültige Schlüssel ergaben
401, der lesbare Konflikt 422 und der erzwungene Werkzeugfehler 500. Nach
Erfolg, Fehlern und abgebrochenem Upload war der Auftragsbereich leer.
Die Windows- und Linux-CLI-Regressionen bestanden nach der Entkopplung ebenfalls.
