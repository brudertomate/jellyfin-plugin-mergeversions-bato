# Versionsreparatur in 1.5.0.0

## Abschlussprüfung vor der Deinstallation (ab 1.5.1.0)

**Verify all movies are split** ist eine eigene, immer rein lesende Aktion. Der Dry-Run-Schalter
hat auf diese Prüfung keinen Einfluss. Sie liest alle Filmobjekte neu ein, einschließlich
versteckter Versionen, ausgeschlossener Bibliotheken und Filmen ohne TMDb-ID. Zusätzlich fragt sie
über `ILinkedChildrenService` sowohl manuelle als auch lokale Datenbank-Versionslinks ab.

Die Prüfung weist Primärverweise, manuelle Datenbanklinks, Versionslisten, lokale Datenbanklinks,
lokale Versionspfade und Filme mit `OwnerId` getrennt aus. Dadurch werden auch lokale Pfade oder
Eigentümerbeziehungen ohne sonstige Versionsgruppe sichtbar. Diese können aus Jellyfins eigener
Dateigruppierung stammen und werden vom Plugin weiterhin nicht automatisch getrennt.

Nur wenn Filme tatsächlich geprüft wurden, keine Lesefehler auftreten und keinerlei solche
Beziehungen verbleiben, erscheint eine grüne Bestätigung zur Deinstallation. Verbliebene
Beziehungen werden gelb gemeldet; eine leere Bibliothek, ein Abbruch oder Lesefehler geben keine
Entwarnung. Ein während der Prüfung erkannter Bibliotheksscan macht das Ergebnis ungültig.
Das Protokoll zeigt höchstens 500 Einträge; **Download split verification (JSON)** enthält alle
Befunde mit IDs, ursprünglichen Beziehungen, Pfaden und gegebenenfalls Fehlerdetails.

Das Ergebnis ist eine Momentaufnahme. Automatischen Merge bis zur Deinstallation deaktiviert
lassen und andere Schreiber während der Prüfung stoppen. Ein nachfolgender Merge-, Reparatur-
oder produktiver Split-Lauf verwirft den gespeicherten Prüfbericht. Nach grüner Bestätigung kann
das Plugin über Jellyfins Plugin-Verwaltung deinstalliert und Jellyfin neu gestartet werden.
Dateien und Metadaten werden bei der Prüfung nicht verändert. Andere Fremdschlüsselverletzungen
und die Existenz der Mediendateien sind nicht Gegenstand dieser Prüfung.

## Ursache der 404-Fehler

Jellyfins `VideosController.DeleteAlternateSources` liest zunächst das Video im Benutzerkontext.
Bei einem Video ohne eigene Alternativliste, aber mit `PrimaryVersionId`, lädt es anschließend
die Primärversion. Beide Lesevorgänge können `NotFound` liefern. Eine fehlende Datei wird dort
nicht geprüft. Beim dokumentierten Baby-Driver-Datensatz erklärt die nicht mehr vorhandene
Primärversion den zweiten 404-Pfad. Andere 404-Ergebnisse können beispielsweise auf ein fehlendes
Ausgangsobjekt oder dessen fehlende Sichtbarkeit im Benutzerkontext zurückgehen.

Quellen: [Jellyfin 12.1 VideosController](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Jellyfin.Api/Controllers/VideosController.cs),
[Versionen-Persistierung](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Jellyfin.Server.Implementations/Item/ItemPersistenceService.cs),
[Hydrierung der Versionslinks](https://github.com/jellyfin/jellyfin/blob/ee91c75e777da41a9c4f4855e70adc604fbf2ef8/Jellyfin.Server.Implementations/Item/BaseItemMapper.cs).

## Vorgehen in der Oberfläche

1. Bibliotheksscans und andere schreibende Verwaltungsaufgaben stoppen. Den automatischen
   Merge-Task vorübergehend deaktivieren, damit er getrennte Versionen nicht wieder zusammenführt.
2. Vor jeder produktiven Reparatur Jellyfin stoppen und die vollständige Datenbank sowie die
   Konfiguration sichern. Bei einer Dateikopie gehören vorhandene WAL-/SHM-Dateien dazu.
   Vorzugsweise zunächst auf einer isolierten Kopie der Installation testen.
3. Jellyfin mit Plugin 1.5.0.0 starten. **Analyze version links** ausführen und anschließend
   **Download full version report (JSON)** wählen.
4. **Dry-run** bleibt zunächst aktiviert. **Repair orphaned versions** zeigt die betroffenen
   inkonsistenten Gruppen; **Split all movies** zeigt zusätzlich gesunde manuelle Versionsgruppen.
5. Bericht prüfen. Für die produktive Aktion Dry-Run ausschalten und die Sicherungsabfrage bestätigen.
6. Fortschritt und Fehler prüfen, Bericht erneut herunterladen und anschließend nochmals analysieren.

Der Bericht enthält alle Gruppen, Objekt-IDs, Pfade, ursprünglichen Primärverweise,
Alternativlinks, Befunde und den Zustand `Healthy`, `Planned`, `Blocked`, `Changed` oder `Failed`.
Er liegt bis zum nächsten Versionslauf bzw. Serverneustart im Speicher. Bei einem Abbruch bleiben
noch nicht bearbeitete Gruppen `Planned`. Während eines Laufs sind Bericht und Zähler Zwischenstände.
Die Bildschirmprotokolle bleiben auf die letzten 500 Einträge begrenzt; der JSON-Bericht enthält
sämtliche Gruppen und vollständige Exceptions fehlgeschlagener Änderungen. Pfade können private
Informationen enthalten; den Bericht vor dem Weitergeben prüfen.

## Reparaturregeln

- Die Abfrage verwendet `IItemRepository`, schließt versteckte Alternativen und eigene/virtuelle
  Filmobjekte ein und deaktiviert die Gruppierung nach Darstellungsschlüsseln. TMDb, aktive
  Bibliothekspfade und Merge-Ausschlüsse begrenzen ausschließlich die bestehende Merge-Funktion.
- `PrimaryVersionId` und manuelle Alternativlinks bilden einen ungerichteten Graphen. Fehlende
  Ziel-IDs verbinden zusammengehörige Restobjekte ebenfalls. Dadurch werden auch umgekehrte,
  einseitige Links, Ketten, Zyklen, Selbstverweise und mehrfach referenzierte Alternativen erfasst.
- Die Reparatur trennt **die ganze inkonsistente Gruppe** in einzelne Filme. Sie versucht nicht,
  eine gewünschte neue Primärversion zu erraten. Gesunde manuelle Gruppen bleiben bei dieser Aktion
  erhalten; **Split all movies** trennt auch diese.
- Fehlende Dateien und Verzeichnisse sind unerheblich. Es gibt weder Dateisystemauflösung noch
  Löschaufrufe. Fehlende Datenbankobjekte werden nicht neu angelegt.
- Lokale, automatisch aus Dateinamen gebildete Versionen und `OwnerId`-Beziehungen werden blockiert.
  Auch lokale Datenbanklinks ohne gespeicherten Dateipfad werden über `ILinkedChildrenService`
  erkannt. Fremde Objekttypen und unerwartete Linktypen blockieren ebenfalls die komplette Gruppe.
  Solche Fälle brauchen eine gesonderte Untersuchung; ein blockierter Kandidat zählt als Fehler.

## Speicherung und Grenzen

Für jede ausgewählte Gruppe liest das Plugin die Filmobjekte erneut aus dem Repository und prüft,
ob die Versionsbeziehungen noch dem Plan entsprechen. Es entfernt die manuellen Alternativlinks
und setzt über `SetPrimaryVersionId(null)` auch den Darstellungsschlüssel zurück. Alle vorhandenen
Gruppenmitglieder werden gemeinsam an `IItemPersistenceService.SaveItems` übergeben.
Die geprüfte Jellyfin-12.1-Implementierung verwendet dafür eine Datenbanktransaktion. Sie erhält
bei Videos andere `LinkedChildren`-Typen. Es gibt keine direkten SQL-Befehle im Plugin.
Die allgemeine Jellyfin-Persistierung speichert auch die unverändert eingelesenen Metadaten;
eine öffentliche, transaktionale Schnittstelle ausschließlich für Versionsfelder existiert hier nicht.

Die Sperre umfasst alle Merge-, Analyse-, Reparatur- und Split-Läufe dieses Plugins. Eine bereits
laufende Bibliotheksanalyse verhindert den Start; vor jeder Änderung erfolgt eine weitere Prüfung.
Das ist **keine globale Jellyfin-Schreibsperre**: andere Plugins, Löschaktionen oder ein genau
gleichzeitig startender Scan können weiterhin konkurrieren. Deshalb müssen andere Schreiber
während einer produktiven Reparatur gestoppt bleiben. Ein einzelner Scan-Fehler beendet nicht die
Bearbeitung unabhängiger Gruppen.

Abbruch greift zwischen Gruppen. Eine begonnene Transaktion wird einschließlich Kontrolllesen
abgeschlossen. Nach einem Schreibfehler werden die in dieser Gruppe geänderten In-Memory-Felder
wiederhergestellt. Nach erfolgreichem Speichern werden die Beziehungen erneut aus dem Repository
gelesen und Bibliothekscacheobjekte angepasst. Ein fehlgeschlagenes Kontrolllesen zählt als Fehler,
auch wenn die Transaktion möglicherweise bereits gespeichert wurde. Eine erneute Analyse zeigt
den tatsächlichen Zustand. Bereits sauber getrennte Gruppen erscheinen beim nächsten Lauf nicht
mehr als Kandidaten.

Diese Version verlangt **Jellyfin 12.1 oder neuer innerhalb der kompatiblen ABI**, entsprechend den
bereits im Projekt verwendeten 12.1-Paketen und den hier geprüften Persistierungsregeln. Das
Release-Manifest wird erst durch einen später freigegebenen Release-Tag aktualisiert.

## Bevorzugte Primärversion und Fremdschlüsselbefunde

Die Auswahl der bevorzugten Auflösung bleibt erhalten. Der zusätzliche
`RerouteLinkedChildReferencesAsync`-Aufruf beim nachträglichen Wechsel der bevorzugten Primärversion
entfällt. Dieser Aufruf betrifft manuelle Sammlung-/Playlist-Verweise und ist für die
Versionsbeziehungen selbst nicht erforderlich. Der normale native Jellyfin-Merge behält seine
eigene Verweisbehandlung. Daraus lässt sich keine Ursache für die vorhandenen Fremdschlüsselfehler
ableiten.

`integrity_check = ok` prüft die strukturelle Datenbankintegrität und widerlegt keine verwaisten
Fremdschlüssel. Die gemeldeten 795 Provider-, 465 Stream-, 26 Keyframe- und eine Personenbeziehung
werden durch dieses Plugin nicht bereinigt. Für eine Ursachenanalyse sind auf einer Sicherung die
jeweiligen `foreign_key_check`-Zeilen mit den betroffenen Eltern-IDs und Serverprotokollen
abzugleichen. Ohne diese Daten lässt sich weder ein konkreter Löschpfad noch die Verursachung
durch ein Plugin belegen. Persistierungsfehler werden mit vollständiger Exception protokolliert,
nicht als erfolgreiche Reparatur verbucht.

## Validierung

`dotnet test -c Release` deckt die bestehende Merge-/Auflösungslogik und die neuen Reparaturregeln
mit Repository-/Persistierungsmocks ab: einschließlich Baby Driver, fehlenden Primär- und
Alternativobjekten, lokalen Datenbanklinks, Zyklen, Dry-Run, Abbruch, Wiederholung, Konkurrenz,
Schreibfehlern und fehlgeschlagenem Kontrolllesen.
Diese Tests ersetzen keinen Integrationstest mit einem laufenden Jellyfin und einer echten
Datenbankkopie. Insbesondere Transaktionsrollback und das Erhalten anderer `LinkedChildren`-Typen
sind anhand der Jellyfin-Implementierung geprüft, nicht durch einen realen Serverlauf nachgewiesen.
Vor produktivem Einsatz auf einer Kopie gesunde und inkonsistente Gruppen aufteilen, die übrigen
Beziehungstypen vergleichen und `foreign_key_check` vor/nach dem Lauf auswerten.
