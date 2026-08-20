# Krones LMS Log Analysis App

Die Anwendung ueberwacht konfigurierte LMS-Logdateien und sammelt neue Fehler in einer gemeinsamen Fehlerdatei. Sie kann als normale Windows-Anwendung oder als Windows-Service ausgefuehrt werden.

## Dateien im Publish-Ordner

```text
Krones.Lms.LogAnalysisApp.exe   Anwendung, selbststaendig lauffaehig
appsettings.json                 Konfiguration der Anwendung
README.md                        Diese Dokumentation
```

Die EXE ist als self-contained Single-File-Anwendung veroeffentlicht. Auf dem Zielsystem muss keine .NET-Runtime installiert sein. `appsettings.json` muss neben der EXE liegen, weil die Logquellen dort angepasst werden koennen.

## Funktionsweise

1. Die Anwendung liest `appsettings.json` beim Start.
2. Die Anwendung prueft beim Start sofort alle eingetragenen Logdateien und danach standardmaessig alle 2 Minuten.
3. Bereits verarbeitete Dateipositionen werden in `State\scanner-state.json` gespeichert.
4. Bei der naechsten Pruefung werden nur neu hinzugekommene vollstaendige Zeilen gelesen.
5. Logeintraege mit dem Status `WARN`, `ERROR` oder `FATAL` werden verarbeitet.
6. Mehrzeilige Eintraege, zum Beispiel Stacktraces, werden zusammengefasst.
7. Die Fehler werden gemeinsam und nach dem letzten Timestamp sortiert in die Ausgabedatei geschrieben.
8. Die Ausgabedatei wird nur bei neu erkannten relevanten Eintraegen aktualisiert. Sie wird atomar ersetzt, damit kein leerer oder halb geschriebener Bericht sichtbar wird.

Die Anwendung liest die Pfade relativ zum Ordner, in dem die EXE liegt. Ein Pfad wie `../Gateway/Log/Gateway.log` bedeutet daher: eine Ebene nach oben und anschliessend in den Ordner `Gateway\Log`.

## Ordnerstruktur

Beispiel:

```text
C:\LMS_v12.6.7.0\
|
+-- LogAnalysisApp\
|   +-- Krones.Lms.LogAnalysisApp.exe
|   +-- appsettings.json
|   +-- README.md
|   +-- Log\
|   +-- State\
|
+-- Gateway\
|   +-- Log\
|       +-- Gateway.log
|
+-- Configuration\
    +-- Log\
        +-- ConfigurationService.log
```

Die Anwendung erstellt die Ordner `Log` und `State` bei Bedarf automatisch. Der Benutzer des Windows-Service benoetigt Leserechte auf die Quelllogs und Schreibrechte auf den Ordner der Anwendung.

## Ausgabedateien

Die Dateien liegen relativ zum EXE-Ordner:

```text
Log\LmsErrorLog.log
Log\LmsErrorLog.log.state.json
State\scanner-state.json
```

`LmsErrorLog.log` ist die gemeinsame Fehlerliste. Doppelte Fehler werden pro Tag anhand ihrer normalisierten Meldung zusammengefasst. Der Service ist nicht Bestandteil des Vergleichsschluessels; derselbe Meldungstext aus verschiedenen Services wird daher als ein Fehler zusammengefasst. Der Timestamp, der Status und der Service entsprechen dem zuletzt erkannten Auftreten. Am Zeilenende steht die Anzahl der heutigen Vorkommen.

Beispiel:

```text
2026-08-20 14:32:10 [FATAL] [Gateway] Verbindung zur Datenbank fehlgeschlagen Count: 4
```

## Neuen Service hinzufuegen

1. Oeffne `appsettings.json` mit einem Texteditor.
2. Fuege im Array `LogAnalysis:Sources` einen weiteren Eintrag hinzu.
3. Setze `Name` auf einen eindeutigen Anzeigenamen.
4. Setze `Path` auf den Logpfad relativ zum EXE-Ordner.
5. Speichere die Datei.
6. Starte die Anwendung beziehungsweise den Windows-Service neu.

Beispiel:

```json
"Sources": [
  { "Name": "Gateway", "Path": "../Gateway/Log/Gateway.log" },
  { "Name": "NeuerService", "Path": "../NeuerService/Log/NeuerService.log" }
]
```

Achte auf gueltiges JSON:

- Jeder Eintrag ausser dem letzten benoetigt ein Komma.
- Windows-Pfade verwenden in JSON entweder `/` oder doppelte Backslashes (`\\`).
- Der Dateiname muss exakt stimmen.

Nach dem Neustart prueft die Anwendung den neuen Service automatisch. Existiert die Datei nicht, wird eine Warnung mit dem berechneten Pfad geschrieben; die anderen Quellen werden trotzdem weiter verarbeitet.

## Wichtige Einstellungen

```json
"LogAnalysis": {
  "PollingIntervalMinutes": 2,
  "StateFilePath": "State/scanner-state.json",
  "ErrorLogPath": "Log/LmsErrorLog.log",
  "Sources": []
}
```

- `PollingIntervalMinutes`: Wartezeit zwischen den Pruefungen. Die erste Pruefung erfolgt direkt beim Start.
- `StateFilePath`: Positionen der zuletzt gelesenen Dateien.
- `ErrorLogPath`: gemeinsame Fehlerausgabe.
- `Sources`: Liste der zu ueberwachenden Services.
- `HeaderRegexPattern`: erkennt Timestamp und Status einer neuen Logzeile. Nur aendern, wenn das Format der Logdateien abweicht.

## Starten

Direkt aus PowerShell oder CMD:

```powershell
cd "C:\LMS_v12.6.7.0\LogAnalysisApp"
.\Krones.Lms.LogAnalysisApp.exe
```

Zum Beenden bei direktem Start `Ctrl+C` druecken.

Als Windows-Service muss der Service mit dem Pfad zur EXE eingerichtet werden. Die Anwendung verwendet dabei den Service-Namen:

```text
Krones.Lms.LogAnalysisApp
```

Nach jeder Aenderung an `appsettings.json` muss der Service neu gestartet werden, da die Konfiguration nur beim Start geladen wird.

## Verhalten bei Neustart und neuen Tagen

Der Scanner merkt sich pro Quelle die bereits gelesene Byteposition in `State\\scanner-state.json`. Dadurch werden nach einem Neustart normalerweise nur neue Logdaten verarbeitet. Wird eine Quelldatei verkleinert oder rotiert, beginnt der Scanner diese Datei wieder am Anfang.

Die Fehlerzusammenfassung wird in `Log\\LmsErrorLog.log.state.json` gespeichert. Beim Start wird dieser Zustand geladen. Eintraege aus vergangenen Tagen werden entfernt, sobald ein neuer relevanter Fehler verarbeitet wird; die Zusammenfassung ist daher eine Tagesliste.

Ein Logeintrag muss mit einer vollstaendigen Zeile enden. Unvollstaendige letzte Zeilen werden bis zur naechsten Pruefung zurueckgestellt. Mehrzeilige Eintraege werden ueber ihren ersten Header erkannt und zusammen mit ihren Folgezeilen verarbeitet.
