# Projektpläne & Roadmap (Übersicht für KI-Sessions)

> **Stand: 07.11.2026** — Konsolidierte Struktur nach Merge des Branches `MajorPhysikFixes` in `main`.  
> **Test- & Buildstatus:** 557/557 Unit-Tests grün (`dotnet test`), 0 Warnungen, 0 Fehler. BIC_12 Server-Deploy verifiziert.

---

## 1. Aktive Dokumente in `.plans/`

Neue Sessions und KI-Modelle sollen sich primär an diesen vier aktiven Dokumenten orientieren:

1. **[`ROADMAP.md`](ROADMAP.md) — Gesamt-Entwicklungsplan (Meilensteine 1–4):**
   - **Meilenstein 1:** 2D-Referenzlauf (`Naca2D`, 5×5) & universelle Python-Auswerteskripte unter `scripts/` (`plot_history.py`, `plot_optimization.py`, `plot_calibration.py`).
   - **Meilenstein 2:** KI- & Machine-Learning-Integration (Bayes-Optimierung via Gauß-Prozess & Expected Improvement, TuRBO, Modus "Metrics").
   - **Meilenstein 3:** Große LaTeX-Gesamtdokumentation & Showcase (3D MantaAuv & 2D NACA, ParaView-Renderings, GCI-Gitterkonvergenz).
   - **Meilenstein 4:** FEM-Integration & Fluid-Struktur-Kopplung (CalculiX `ccx` als zweite `SolverStage`).

2. **[`ki_optimierung.md`](ki_optimierung.md) — Detaillierte KI-Architektur & TODOs:**
   - Block G–I: TODO-53 bis TODO-60 (Surrogatmodellierung, Gauß-Prozess in C#, Expected Improvement, Vertrauensbereiche und Praxistests).

3. **[`fem_ausblick.md`](fem_ausblick.md) — Merkzettel & Schnittstellendesign für FEM:**
   - Multi-Stage-Erweiterung (`"Stages": ["Cfd", "Fem"]`), CalculiX (`ccx`), Drucklasttransfer und Randbedingungs-Zonen.

4. **[`Anmerkungen.txt`](Anmerkungen.txt) — Rohnotizen & fortlaufendes Feedback:**
   - Enthält historische und aktuelle Benutzerwünsche (u. a. Zeilen 44–61 als Basis der Roadmap).

---

## 2. Historisches Archiv in `.plans/.old/`

*Zur Schonung des Kontextfensters/Token-Verbrauchs müssen folgende Dateien in neuen Sessions **nicht** mehr eingelesen werden:*

* [`.plans/.old/architektur_refactoring.md`](.old/architektur_refactoring.md) — Entscheidungen 1–8 & TODO-1 bis 52 (Architektur, Multi-Stage, Journaling) vollständig umgesetzt.
* [`.plans/.old/projektbuendel.md`](.old/projektbuendel.md) — Multi-Projekt-Struktur (`src/projects/`), Vorlagen (`_Vorlage`, `_Vorlage2D`) und Skripte etabliert.
* [`.plans/.old/anmerkungen_umsetzung.md`](.old/anmerkungen_umsetzung.md) — Frühe Anmerkungen & Feedbackpunkte abgearbeitet.
* [`.plans/.old/architektur_kritikpunkte_naca.md`](.old/architektur_kritikpunkte_naca.md) — KP-1 bis KP-7 vollständig behoben (RubberBandScaler, ChampionValidator, AoA-Optimierung, Resolver).
* [`.plans/.old/physik_feinschliff.md`](.old/physik_feinschliff.md) — Block W (TODO-W1 bis W10: adaptive Kalibrierung, Detailbox, Knickschutz B14, URANS-Restart) vollständig abgeschlossen.
* [`.plans/.old/servertest_2026-11-06.md`](.old/servertest_2026-11-06.md) — Servertest Ketten 1–3 auf BIC_12 ausgewertet; Folgefehler B1–B17 vollständig behoben.

---

## 3. Aktueller Systemstatus

* **Physik & Vernetzung:** 2D- und 3D-CFD (SU2) mit Wandauflösung ($y^+ \approx 1$), adaptiver Kalibrierung, Nachlauf-Detailbox und Knickpunkt-Schutz an scharfen Hinterkanten (B14).
* **Pipeline & Algorithmen:** RSM, Evolutionäre Optimierung, robuster Champion-Validator mit relativem Jitter und Parameter-Bounds-Clamping (KP-2), aktive Anstellwinkel-Optimierung (KP-4), plattformunabhängiger `ExecutableResolver` (KP-7).
* **Konfiguration:** `MachNumber` vollständig durch `FreestreamVelocity` abgelöst; saubere Projekt-JSONs ohne störende Warnungen.
