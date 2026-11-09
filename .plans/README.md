# Projektpläne & Roadmap (Übersicht für KI-Sessions)

> **Stand: 08.11.2026 (abends)** — Meilenstein 1 abgeschlossen und archiviert, **Meilenstein 2 (KI-Integration) auf Branch `KI_Integration` umgesetzt und ausgewertet**; Aufräum-Durchgang erledigt.  
> **Test- & Buildstatus (09.11.2026):** 683/683 Unit-Tests grün (`dotnet test OptimierungsAlgoCFD.sln`), 0 Warnungen, 0 Fehler. Vergleichsläufe auf BIC_12 (5×5 und 10×10) ausgewertet; der Server-Deploy mit dem neuen Ordnernamen steht noch aus.  
> **Namen:** Framework `src/OptimierungsAlgoCFD/` (Namespace `OptimierungsAlgoCFD`, wie das GitHub-Repository), Tests `tests/OptimierungsAlgoCFD.Tests/`, Vorlagen `_Vorlage3D` und `_Vorlage2D`. Frühere Pläne in `.old/` nennen noch die alten Namen (`Automatisierung_v2`, `MyPicoGkProject`, `_Vorlage`, `TestprojektNACA`).

---

## 1. Aktive Dokumente in `.plans/`

Neue Sessions und KI-Modelle sollen sich primär an diesen aktiven Dokumenten orientieren:

1. **[`ROADMAP.md`](ROADMAP.md) — Gesamt-Entwicklungsplan (offen: Meilenstein 3 und 4):**
   - **Meilenstein 2 (KI, umgesetzt):** Bayes-Optimierung via Gauß-Prozess & Expected Improvement, TuRBO, Modus "Metrics". Bleibt als Referenz in der Roadmap markiert.
   - **Meilenstein 3:** Große LaTeX-Gesamtdokumentation & Showcase (3D MantaAuv & 2D NACA, ParaView-Renderings, GCI-Gitterkonvergenz).
   - **Meilenstein 4:** FEM-Integration & Fluid-Struktur-Kopplung (CalculiX `ccx` als zweite `SolverStage`).

2. **[`automatisches_ende.md`](automatisches_ende.md) — AutoStop (09.11.2026, umgesetzt):** Lauf endet früher, wenn sich der Bestwert über die letzten N Varianten um weniger als X % verbessert; `MaxIterations` × `VariantsPerIteration` bleibt die Obergrenze. Offen: ein Serverlauf mit `"Mode": "On"`.

2b. **[`llm_geometrie_bewertung.md`](llm_geometrie_bewertung.md) — AP 2.8:** Architektur-Bewertung für LLM-gestützte Geometriegenerierung (reine Bewertung, kein Umsetzungsauftrag).

3. **[`fem_ausblick.md`](fem_ausblick.md) — Merkzettel & Schnittstellendesign für FEM:**
   - Multi-Stage-Erweiterung (`"Stages": ["Cfd", "Fem"]`), CalculiX (`ccx`), Drucklasttransfer und Randbedingungs-Zonen.

4. **[`Anmerkungen.txt`](Anmerkungen.txt) — Rohnotizen & fortlaufendes Feedback:**
   - Rohnotizen des Nutzers; die Vorgaben für den LaTeX-Bericht und die englische Fassung sind in `ROADMAP.md` (Meilensteine 3 und 5) übernommen. Diese Datei wird nicht von Claude verändert.

---

## 2. Historisches Archiv in `.plans/.old/`

*Zur Schonung des Kontextfensters/Token-Verbrauchs müssen folgende Dateien in neuen Sessions **nicht** mehr eingelesen werden:*

* [`.plans/.old/architektur_refactoring.md`](.old/architektur_refactoring.md) — Entscheidungen 1–8 & TODO-1 bis 52 (Architektur, Multi-Stage, Journaling) vollständig umgesetzt.
* [`.plans/.old/projektbuendel.md`](.old/projektbuendel.md) — Multi-Projekt-Struktur (`src/projects/`), Vorlagen (`_Vorlage3D`, `_Vorlage2D`) und Skripte etabliert.
* [`.plans/.old/anmerkungen_umsetzung.md`](.old/anmerkungen_umsetzung.md) — Frühe Anmerkungen & Feedbackpunkte abgearbeitet.
* [`.plans/.old/architektur_kritikpunkte_naca.md`](.old/architektur_kritikpunkte_naca.md) — KP-1 bis KP-7 vollständig behoben (RubberBandScaler, ChampionValidator, AoA-Optimierung, Resolver).
* [`.plans/.old/physik_feinschliff.md`](.old/physik_feinschliff.md) — Block W (TODO-W1 bis W10: adaptive Kalibrierung, Detailbox, Knickschutz B14, URANS-Restart) vollständig abgeschlossen.
* [`.plans/.old/ms1_auswertung_und_logdesign.md`](.old/ms1_auswertung_und_logdesign.md) — Meilenstein 1 (Python-Auswerteskripte, Log-Redesign, 10×10-Referenzlauf `Naca2D`) vollständig abgeschlossen.
* [`.plans/.old/ki_optimierung.md`](.old/ki_optimierung.md) — KI-Architektur, TODO-53 bis 60 (Bayes, Gauß-Prozess, TuRBO, Metrics-Modus) umgesetzt; am Ende der Umsetzungsstand mit allen Abweichungen.
* [`.plans/.old/vergleich_optimierer.md`](.old/vergleich_optimierer.md) — Vergleichsläufe Rsm vs. Evolution vs. Bayes (`Naca2D`, 5×5 und 10×10, drei Seeds): Bayes klar am besten, seitdem Standard.
* [`.plans/.old/servertest_2026-11-06.md`](.old/servertest_2026-11-06.md) — Servertest Ketten 1–3 auf BIC_12 ausgewertet; Folgefehler B1–B17 vollständig behoben.

---

## 3. Aktueller Systemstatus

* **Physik & Vernetzung:** 2D- und 3D-CFD (SU2) mit Wandauflösung ($y^+ \approx 1$), adaptiver Kalibrierung, Nachlauf-Detailbox und Knickpunkt-Schutz an scharfen Hinterkanten (B14).
* **Pipeline & Algorithmen:** RSM, Evolutionäre Optimierung, **Bayes-Optimierung (Gauß-Prozess, EI, Metrics-Modus, Vertrauensbereich)**, robuster Champion-Validator mit relativem Jitter und Parameter-Bounds-Clamping (KP-2), aktive Anstellwinkel-Optimierung (KP-4), plattformunabhängiger `ExecutableResolver` (KP-7).
* **Konfiguration:** `FreestreamVelocity` (m/s) ist der Standardweg zur Anströmgeschwindigkeit; `MachNumber` bleibt nur als veralteter Rückfall (`FreestreamVelocity = 0`). Die Vorlagen sind auf genaue Rechnungen ausgelegt (Auto-Netzkalibrierung, strenge Konvergenz, Feinprüfung der besten Varianten, instationär-stabile Rechnungen gültig).
