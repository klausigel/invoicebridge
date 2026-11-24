# Lokaler InvoiceBridge-HTTP-Test

Die lokale Startseite unter `http://127.0.0.1:8080/` zeigt zunächst die
Anmeldung. Nach einmaliger Eingabe des API-Schlüssels zeigt die Arbeitsansicht
den angemeldeten Mandanten und „Abmelden“. Ein Reload übernimmt die gültige
Sitzung; „Abmelden“ widerruft sie im Server und führt zurück zur Anmeldung.
Der Browser sendet den API-Schlüssel nur beim Login im JSON-Body und legt
ihn weder in URLs noch im Browserspeicher ab.

Das Cookie `invoicebridge_session` ist hostgebunden, `HttpOnly` und
`SameSite=Strict`. Bei HTTPS setzt die App zusätzlich `Secure`; das
bisherige lokale HTTP bleibt auf Loopback nutzbar. Die zufällige Kennung wird
per Cookie zugestellt und serverseitig nur als Prüfsumme gehalten. Die
Sitzung gilt absolut 30 Minuten, ein Prozessneustart verwirft sie. Die
optionale lokale Einstellung `INVOICEBRIDGE_SESSION_SECONDS` dient unter
anderem dem Ablauf-Test.

Login, Logout und Verarbeitung mit Sitzung brauchen einen passenden
`Origin` mit gleichem Schema, Host und Port. Ein vorhandenes
`Sec-Fetch-Site` muss `same-origin` sein. Maschinenaufrufe per
`X-Api-Key` behalten ihren bisherigen Vertrag und brauchen keinen
Browser-Origin. Ein expliziter ungültiger Schlüssel fällt nicht auf eine
parallel vorliegende Sitzung zurück.

| Aktion | Ablauf |
| --- | --- |
| PDF umwandeln | Die freigegebene Vorlage hochladen und die geprüfte ZUGFeRD-PDF herunterladen. Der bisherige API-Vertrag bleibt erhalten. |
| ZUGFeRD-Rechnung prüfen | PDF öffnen und Hybridstruktur, PDF/A-3b, CII-XML-Schema und XML-Geschäftsregeln getrennt anzeigen. Die Ansicht unterscheidet fachlich nicht bestanden, nicht ausgeführt und technische Fehler. |
| GiroCode erstellen | Aus ZUGFeRD-PDF, CII-XML oder der unterstützten normalen PDF lesen. Eindeutige Felder übernehmen, leere Felder ergänzen, Zahlungsdaten bestätigen und den Code neben dem Klartext anzeigen. Die Quelldatei bleibt unverändert. |

Die formale Prüfung richtet sich nach dem bestehenden Zielprofil EN 16931
(CII und PDF/A-3b). Das Auslesen im GiroCode-Weg ist keine formale Prüfung.
Manuelle Codes unterstützen EUR und deutsche IBANs; BIC ist optional. Eine
mehrdeutige Auslesung befüllt keine Zahlungsfelder. Bei normalen PDFs werden
keine Mandanten-Fallbackdaten in das GiroCode-Formular übernommen.

Zusätzliche gleichoriginige Endpunkte:

- `POST /v1/session/login`: JSON mit `apiKey`, Antwort mit `tenant` und Cookie.
- `GET /v1/session`: gibt den Mandanten einer gültigen Sitzung zurück; sonst 401.
- `POST /v1/session/logout`: widerruft die Sitzung und löscht das Cookie (204).
- `POST /v1/validate`: PDF im Body. JSON mit `status` und `checks`.
  HTTP 200 auch bei einer fachlich ungültigen Datei. HTTP 503 bei einem
  technischen Prüferfehler inklusive der bereits erzielten Ergebnisse.
- `POST /v1/payment/read`: PDF oder CII-XML im Body. JSON mit `fields` und
  `source`. HTTP 422 bei Mehrdeutigkeit oder unlesbaren Eingaben.
- `POST /v1/payment/qr`: JSON mit Empfänger, IBAN, BIC, EUR-Betrag und
  Referenz (`beneficiary`, `iban`, `bic`, `amount`, `reference`). Antwort
  mit normalisierten `payment`-Feldern und PNG (`png`, Base64).

Der ASP.NET-Core-Dienst verwendet den bestehenden Rechnungsmuster-, Konvertierungs-
und Prüfablauf der CLI. Er wählt pro Upload die Zahlungsdaten für den per API-Key
authentifizierten Mandanten. Der Umfang bleibt auf das bekannte einseitige
Rechnungsmuster begrenzt.

- Genau ein lesbarer, freigegebener IBAN-/BIC-Block im vorgesehenen Bereich
  bleibt als vorhandener PDF-Text erhalten. Die gelesene IBAN und BIC gehen ins
  XML und in den GiroCode. Die Seite wird für die Ausgabe nicht gerastert.
  Der Bildvergleich rendert sie nur zur Prüfung. Ein belegter Bereich für den
  GiroCode führt zur Ablehnung.
- Für Manuels exakt bekanntes grafisches Dokument greift das konfigurierte
  Fallback. Das setzt voraus, dass ein Bild den bekannten Bankbereich berührt
  und im Dokument keine lesbaren Bankangaben stehen. Der alte Block wird aus
  den Bilddaten entfernt. IBAN und BIC aus dem Profil stehen danach sichtbar im
  Ersatzblock, im XML und im GiroCode. Der übrige Seiteninhalt bleibt optisch
  erhalten, seine Textebene entfällt bei diesem Zweig.
- Ein zusätzlich lokal überprüftes grafisches Dokument kann über
  `approvedGraphicSha256` am jeweiligen Profil exakt freigegeben werden. Dieses
  Feld gehört in die private Laufzeitkonfiguration. Eine Freigabe setzt eine
  überprüfte Lage des sichtbaren Blocks in der bestehenden Rechteckposition
  voraus und gilt ausschließlich für exakt dieselben Dateibytes.
- Widersprüche, mehrere Kontoangaben, verdeckter oder falsch platzierter
  Banktext sowie ein fehlender oder nicht freigegebener Grafikblock werden
  abgewiesen. Es findet keine OCR statt.

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
| 422 | `payment_ambiguous` | Mehr als eine plausible Zahlungsangabe |
| 422 | `payment_unclear` | Bankblock fehlt, ist unklar oder für Austausch nicht freigegeben |
| 422 | `unsupported_pdf` | PDF außerhalb des Rechnungsmusters oder belegter GiroCode-Bereich |
| 429 | `busy` | Beide Verarbeitungsslots belegt |
| 500 | `tool_failed`, `conversion_failed` | Werkzeugfehler oder fehlgeschlagene Verarbeitung/Validierung |
| 504 | `conversion_timeout` | Gesamtes Verarbeitungsbudget überschritten |

Der Dienst legt pro Upload ein privates Arbeitsverzeichnis im tmpfs an.
Er entfernt es vor der PDF-Antwort und bei behandelten Fehlern einschließlich
abgebrochener Uploads. Die PDF-Antwort ist im Speicher auf 4 MiB begrenzt.
Der Container läuft mit schreibgeschütztem Root-Dateisystem und abgeschaltetem
Docker-Logtreiber. Logs und Fehler enthalten keine Nutzdaten oder Schlüssel.
Der Werkzeug-Image-Build enthält ausschließlich Werkzeuge.

## Abnahme nachstellen

```powershell
& $python .\scripts\http-container.py test
```

Der Test erzeugt seine synthetischen PDF-Muster, Wegwerfschlüssel und zwei
fiktive Profile im Speicher. Er benötigt keine echte Rechnungsdatei und kein
Referenz-XML. Er prüft Zahlbeträge und Steuer gegen die synthetische Vorgabe,
extrahiert erneut XML und sichtbaren PDF-Text und ruft Mustang und veraPDF
unabhängig auf. An den zwei erfolgreichen Zweigen prüft er durchsuchbaren
Text für KIG, den sichtbaren Fallback für Manuel, den Gleichlauf zum XML und
die Abwesenheit des früheren Testhinweises. Er prüft außerdem Fehlpfade,
Mandantentrennung bei überlappenden Uploads, Upload-Abbruch und die Bereinigung.

Die lokale Containerabnahme dieser Änderung bestand in 63,265 Sekunden.
Beide Zweige lieferten HTTP 200 und bestanden die unabhängige Mustang- und
PDF/A-3b-Prüfung. Die kontrollierten Fehler lieferten HTTP 401, 422 oder 500
und hinterließen keine Arbeitsdateien. Windows-CLI-Publish und `--help`
bestanden ebenfalls. Für diese Abnahme kamen ausschließlich synthetische
HTTP-Fixtures zum Einsatz.

Der anschließende Web-/HTTP-Regressionstest bestand in 74,297 Sekunden.
Er umfasste gültige, ungültige und technisch nicht prüfbare Dateien, Vorbefüllung
aus eingebettetem und separatem XML sowie normaler PDF, mehrdeutige Eingaben
und den unabhängig dekodierten EPC-Code. Mandantentrennung, Upload-Abbruch und
Bereinigung wurden erneut geprüft. In Edge wurden zusätzlich der tatsächliche
PDF-Download, beide formalen Ergebnisanzeigen, Tastaturnavigation, manuelle
Ergänzung und Bestätigung vor der GiroCode-Anzeige bei Desktop- und Mobilbreite
geprüft. Alle Prüfdokumente und Profile waren synthetisch.

Die anschließende Sitzungsabnahme bestand in Edge und in der synthetischen
HTTP-Regression (84,813 Sekunden für den Abschnitt mit den Rechnungswegen).
Der Test enthält falsche und gültige Anmeldung, Mandantenwechsel, serverseitig
widerrufene Kennungen, Ablauf mit einer Sekunde Sitzungsdauer, fremde oder
fehlende Origins und die unveränderte Verarbeitung mit API-Key-Header.

Für den nächsten F5-Lauf die Serveranwendung neu bauen und starten. Falls
der Debug-Lauf die Webdateien wie bisher aus dem Ausgabeordner bezieht,
`index.html`, `site.css` und `site.js` aus `server/wwwroot` in
`server/bin/Debug/net10.0/wwwroot` aktualisieren und den Browser mit Strg+F5
neu laden. Das vorhandene Startprofil bleibt verwendbar.
