# Lizenzen und Quellen

Eigener Code in diesem POC: SPDX `AGPL-3.0-or-later`. Vollständiger Lizenztext in
`LICENSE`. Quelltextbereitstellung und Einhaltung der nötigen Lizenzbedingungen
sind von Klaus für diesen Weg freigegeben. Ein CLI-Prozessaufruf wird nicht als
pauschale Umgehung von Copyleft-Pflichten behandelt.

| Komponente | Version / Lizenz | Originalquelle und Hinweise |
| --- | --- | --- |
| PdfPig | 0.1.15 / Apache-2.0, enthaltene Dritthinweise | https://github.com/UglyToad/PdfPig, Quellcommit f131f642976936e06ee91cb19d3ed728f9dd18b6; `licenses/PdfPig-LICENSE.txt`, `licenses/PdfPig-NOTICES.txt` |
| ZXing.Net | 0.16.11 / Apache-2.0 | Michael Jahn, Copyright 2012; .NET-Port von ZXing; https://github.com/micjahn/ZXing.Net; Original-NuGet-Paket unter `.packages/zxing.net/0.16.11`, Hash im Lockfile; Apache-Lizenztext in `licenses/ZXing.Net-Apache-2.0.txt` |
| ZUGFeRD-csharp | 18.0.1 / Apache-2.0 | Copyright STwo Industries GmbH 2026; https://github.com/stephanstapel/ZUGFeRD-csharp; NuGet-Quellcommit 5c5ab8d2d3f5b119193b73850633ee4525428cef; `licenses/ZUGFeRD-csharp-Apache-2.0.txt` |
| Ghostscript | 10.08.0 / AGPL-3.0-or-later, Drittlizenzen | Artifex Software; https://ghostscript.com/licensing/; `licenses/Ghostscript-COPYING.txt`; unveränderte lokale Programmdateien einschließlich ICC-Profil |
| Mustangproject | 2.26.0 / Apache-2.0 mit Drittlizenzen | Copyright Jochen Staerk; https://github.com/ZUGFeRD/mustangproject/tree/core-2.26.0; `licenses/Mustang-LICENSE.txt`, `licenses/Mustang-NOTICE.txt` |
| veraPDF | 1.30.3 / MPL-2.0+ oder GPL-3.0+ | https://github.com/veraPDF/veraPDF-apps/tree/v1.30.3; `licenses/veraPDF-LICENSE.MPL`, `licenses/veraPDF-LICENSE.GPL` |

Mustang enthält unter anderem PDFBox, veraPDF-Bibliotheken, Saxon-HE und
EN16931-Prüfarbeitsstände. Die Original-NOTICE listet diese und ihre jeweiligen
Lizenzen; Apache-2.0 ist keine pauschale Lizenz für sämtliche gebündelten Inhalte.
Die Hersteller-NOTICE enthält selbst eine EUPL-Versionsangabe; sie bleibt als
Originalhinweis unverändert und ist nicht als eigene juristische Einordnung zu
verstehen. Zusätzliche im JAR vorhandene Hinweise sind unter `licenses/mustang-jar`
bewahrt. Entsprechende Hinweise der veraPDF-JARs unter `licenses/verapdf-jars`.

Die passenden Ghostscript-Quellen werden für die spätere Zusammenstellung als
offizielles Archiv `.tools/ghostscript-10.08.0.tar.xz` lokal bereitgehalten:
https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10080/ghostscript-10.08.0.tar.xz
Das Archiv enthält die Quellen, Build-Dateien, Lizenz- und Dritthinweise.
Vor Weitergabe von Binärdateien sind die AGPL-Pflichten für Corresponding Source
zu erfüllen; ein Downloadlink allein wird hier nicht als vollständige Übergabe
bezeichnet. Keine Weitergabe ist erfolgt.

Das Deploymentpaket ergänzt eine portable .NET-10.0.14-Laufzeit aus den
ausdrücklich MIT-lizenzierten offiziellen NuGet-Paketen
`Microsoft.NETCore.App.Runtime.win-x64` und dem veröffentlichten AppHost aus
`Microsoft.NETCore.App.Host.win-x64`. Die NuGet-Metadaten, MIT-Lizenztexte und
Third-Party-Notices werden unter `licenses/dotnet` unverändert beigefügt. Das
anders lizenzierte Microsoft-Runtime-ZIP und die installierte Oracle-Java-
Laufzeit werden nicht in das Paket kopiert.

Java im Paket: unverändertes Eclipse Temurin JRE 21.0.13+9, GPLv2 mit Classpath
Exception. Original-`NOTICE` und `legal` bleiben unter `tools/java` erhalten,
das passende offizielle Quellarchiv liegt unter `sources`. Die gehashten
Herstellerarchive und Metadaten verbleiben außerdem lokal unter `.tools`.
Quellen der eingebetteten veraPDF-Versionen und Saxon-HE 12.8/12.10 sowie der
Mustang-2.26.0-Quellbaum werden ebenfalls beigefügt. Dies erfüllt die Vorbereitung
einer unveränderten Weitergabe mit Quellen/Hinweisen, ohne einen zusätzlichen
kommerziellen Vertrag abzuschließen oder eine Haftungsfreistellung zu akzeptieren.

Python, Pillow, pypdf, Poppler, SDK und 7-Zip sind nur Werkzeuge zur Entwicklung
und zusätzlichen QA auf diesem Desktop. Sie werden nicht weiterverteilt und
sind für den eigentlichen paketierten CLI-Lauf nicht erforderlich.
Keine Lizenzkäufe und keine kommerziellen Testlizenzen wurden verwendet.
