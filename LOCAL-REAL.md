# Lokaler Test mit eigenen Mandantenprofilen

Der bisherige Modus startet standardmäßig mit synthetischen Profilen.
`local-real` ist eine ausdrückliche Auswahl für einen lokalen Windows-/F5-Lauf
auf Loopback. Dieser Modus validiert die Form der deutschen IBAN, BIC,
Kontoinhaber und Bankname. Er prüft weder die Existenz des Kontos noch seine
Zahlungsberechtigung. Die Freigabe der Bankdaten liegt bei Klaus.

## Profil in Visual Studio

Im HTTP-Projekt liegt die Vorlage `server/tenants.example.json`. Die lokale
Datei `server/tenants.real.local.json` erscheint ebenfalls im Projekt.
Sie ist Git-ignoriert und wird weder in die Web-Ausgabe gebaut noch publiziert
oder als statische Datei ausgeliefert. Falls sie in einem weiteren Checkout
noch fehlt, kann sie einmal aus der Vorlage angelegt werden. Der folgende
Befehl überschreibt keine vorhandene Datei:

```powershell
[IO.File]::Copy(
    (Resolve-Path .\server\tenants.example.json).Path,
    (Join-Path (Get-Location) 'server\tenants.real.local.json'),
    $false)
```

Die IDs bleiben `KIG` und `Manuel`. Für beide unterschiedliche IBANs und
Schlüssel eintragen. Die Profile erfordern die bestätigten Stammdaten
`iban`, `beneficiary`, `bic` und `bankName`. Die CLI-Dateien
`invoicebridge.json` und `invoicebridge.example.json` bleiben davon getrennt.

Einen neuen Schlüssel kann Klaus jeweils lokal in PowerShell 7 erzeugen,
ohne ihn im Terminal auszugeben. Für jeden Mandanten den Befehl einmal
ausführen und den Wert direkt ins jeweilige `apiKey`-Feld einfügen:

```powershell
Set-Clipboard -Value ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
# Nach dem Einfügen die Zwischenablage wieder leeren:
Set-Clipboard -Value ''
```

## F5 und lokaler Port

Im gewählten HTTP-Startprofil diese Werte setzen. Die bisherige Windows-
Werkzeugkonfiguration und das persönliche Profil ansonsten beibehalten.

| Feld im Startprofil | Wert |
| --- | --- |
| `applicationUrl` | Zum Beispiel `http://127.0.0.1:8185`. Genau eine HTTP-Loopback-URL mit explizitem Port. |
| `environmentVariables.INVOICEBRIDGE_PROFILE_MODE` | `local-real` |
| `environmentVariables.INVOICEBRIDGE_PROFILES` | Absoluter Pfad, hier `C:\git\kig\netproof\invoicebridge\server\tenants.real.local.json` |
| `environmentVariables.INVOICEBRIDGE_TOOLS` | Bisheriger absoluter Windows-Werkzeugordner |

F5 drücken und genau die konfigurierte Adresse öffnen, einschließlich Port und
Hostname. Ohne URL-Vorgabe gilt `http://127.0.0.1:8080`. Gleichwertig zum
F5-Port ist `ASPNETCORE_URLS` für einen lokalen Start außerhalb von VS.
Der Browser muss für Login und Sitzungsaktionen dieselbe Herkunft verwenden.
Für einen erneuten synthetischen Test den Profilmodus auf `synthetic` setzen
oder entfernen und `INVOICEBRIDGE_PROFILES` auf die bisherige Testdatei zeigen
lassen. Der Container bleibt intern bei 8080 und unterstützt synthetische
Profile. Den externen Host-Port setzt der Launcher.

## Was eine echte Rechnung im ersten Test erfüllen muss

Der lokale HTTP-Dienst unterscheidet zwei eng abgegrenzte A4-Layouts. Der
Manuel-/NetProof-Pfad prüft weiterhin das bekannte Einzelfallmuster:

- Rechnungssteller `NetProof GmbH` in der erwarteten Adressform mit
  `21376 Salzhausen`. Der Kontoinhaber muss dazu passen. Die Mandanten-ID
  bestimmt den Zugriff unabhängig vom Rechnungssteller.
- Rechnungsnummer `112684`, genau der erwartete HelloExchange-Positionsbereich
  mit Stückeinheit, Vorkasse und regulärer USt von 19 Prozent. Beträge und
  Rundung müssen stimmen und der Bruttowert muss 456,01 EUR betragen.
  Der bisher freigegebene Fall nutzt DE/DE und keine geleistete Zahlung.
- Der lesbare IBAN-/BIC-Block muss im erwarteten Bereich liegen und eindeutig
  sein. Abweichende Zahlungsdaten liefern HTTP 422. Der GiroCode braucht im
  lesbaren Zweig seinen freien Platz. Sichtinhalt und Text bleiben überprüfbar.
- Der grafische Zweig ist eine Einzeldateifreigabe, keine Layout-Erkennung.
  Die bekannte Manuel-Datei ist im lokalen Realmodus nur mit der bereits
  geprüften Bankverbindung freigegeben. Ihr Fingerabdruck lässt sich über
  `approvedGraphicSha256` nicht auf ein anderes Konto umbiegen. Eine andere
  Grafik benötigt eine eigene fachliche und visuelle Prüfung des Bankblocks
  und erst danach ihren exakten SHA-256 in dem passenden Mandantenprofil.
  `approvedGraphicSha256` ansonsten auf `null` lassen.

Beim KIG-Layout steht KIG als Aussteller und NetProof als Rechnungsempfänger
auf einem einzelnen Beleg. Der Dienst liest genau eine EUR-Position mit dem
ausgewiesenen Erlösanteil, die Nettosumme, reguläre Umsatzsteuer und Bruttosumme.
„Sofort fällig“ wird mit dem Ausstellungsdatum abgebildet. Eine gedruckte
Referenz bleibt als Hinweis erhalten, während die Rechnungsnummer den freien
Verwendungszweck bildet. Ein separates Leistungsdatum ist für dieses Layout
unbekannt und bleibt leer.

Das KIG-Profil im Modus `local-real` kann eine niederländische IBAN mit
passendem BIC hinterlegen. Der sichtbare Textblock der KIG-Rechnung muss mit
dem Konto und Kontoinhaber im Profil sowie dem Aussteller übereinstimmen.
Beim BIC-Vergleich sind ein BIC8 und derselbe BIC mit dem Zusatz `XXX`
gleichwertig. XML und GiroCode übernehmen den BIC aus dem sichtbaren Beleg.
Abweichende Kontodaten liefern HTTP 422 (`payment_conflict`), ein Aufruf
mit dem Manuel-Profil liefert HTTP 403 (`tenant_template_mismatch`).
Mehrseitige Sammlungen werden abgewiesen. Für eingebettete GiroCodes muss
der reservierte Bereich frei sein. `?giroCode=false` erzeugt das validierte
Hybrid-PDF ohne neu eingefügten GiroCode.

### Abnahme vor Ort

1. Als KIG anmelden und einen einzelnen Beleg des beschriebenen KIG-Layouts
   in „PDF umwandeln“ auswählen. Bei HTTP 422 den Fehlertext beachten.
2. Für Manuel ab- und erneut anmelden. Wenn die bekannte Originaldatei und
   die dazu geprüfte Bankverbindung vorliegen, diesen Einzelfall zuerst testen.
   Eine abweichende sichtbare oder strukturierte Bankverbindung ist ein
   Prüfgrund. Sie soll keine Konfigurationsänderung auf Verdacht auslösen.
3. Eine erfolgreiche Ausgabe unter „ZUGFeRD-Rechnung prüfen“ kontrollieren.
   Danach bei „GiroCode erstellen“ die Ausgabe zur Vorbefüllung auswählen und
   sichtbare Rechnung, Empfänger, Konto, Betrag und Referenz vergleichen.
   Für einen Rückbericht reichen Mandant, Aktion, Prüfstatus und Fehlercode.

Die reproduzierbaren Tests verwenden erzeugte Belege und fiktive Konten.
Mit dem HTTP-Publish-Ordner und Windows-Werkzeugordner lassen sie sich mit
`scripts/test-local-profiles.py --publish <Ordner>` und
`scripts/test-kig-invoice.py --publish <Ordner>` wiederholen. Der KIG-Test
benötigt Python mit pypdf, ReportLab und Pillow sowie Poppler (`pdftoppm`).
