# Projektpläne & Roadmap (Übersicht für KI-Sessions)

> **Stand: 13.11.2026** — Meilenstein 1 & 2 (KI-Integration) abgeschlossen, **Netzstrategie & SU2-Konvergenz (AP-1 bis AP-7) umgesetzt**; Server-Validierung `MantaAuv` steht aus.  
> **Test- & Buildstatus (13.11.2026):** 787/787 Unit-Tests grün (`dotnet test OptimierungsAlgoCFD.sln`), 0 Warnungen, 0 Fehler.  
> **Namen:** Framework `src/OptimierungsAlgoCFD/` (Namespace `OptimierungsAlgoCFD`, wie das GitHub-Repository), Tests `tests/OptimierungsAlgoCFD.Tests/`, Vorlagen `_Vorlage3D` und `_Vorlage2D`. Frühere Pläne in `.old/` nennen noch die alten Namen (`Automatisierung_v2`, `MyPicoGkProject`, `_Vorlage`, `TestprojektNACA`).

---

## 1. Aktive Dokumente in `.plans/`

Neue Sessions und KI-Modelle sollen sich primär an diesen aktiven Dokumenten orientieren:

1. **[`netzstrategie_und_su2_abbruch.md`](netzstrategie_und_su2_abbruch.md) — Netzstrategie & SU2-Konvergenz (AP-1 bis AP-7 umgesetzt):**
   - SU2 `CauchyTolerance = "1e-5"` & Steady Auto-Restart (`AutoRestartOnDrift`, `RESTART_SOL= YES`), `scaleFactor`-konsistenter `GmshCfdMesher` (reale mm), physikalischer Windkanal ($\ge 5\,L / \ge 15\,L / \ge 5\,B/H / \le 0{,}3\,\%$ Blockage), $y^+ \approx 1{,}5$ & `BoundaryLayerGrowthRate = 1.28`, `MeshSizeFromCurvature = true` (+ `CurvatureMinSizeFactor = 0.5`) & `DetailBox` (`DetailBoxLengthFactor = 1.5`, `DetailBoxMinHeightLengthFactor = 0.15`), `AutoVoxelResolution` / `VoxelResolutionAdvisor`, `--resume`-`DurationSeconds`-Erhalt sowie `Su2_CD_p`/`Su2_CD_v` & Zellklassen-Telemetrie (`MeshCells_*`). **Offen:** Server-Validierung `MantaAuv`.

2. **[`ROADMAP.md`](ROADMAP.md) — Gesamt-Entwicklungsplan (offen: Meilenstein 3 und 4):**
   - **Meilenstein 2 (KI, umgesetzt):** Bayes-Optimierung via Gauß-Prozess & Expected Improvement, TuRBO, Modus "Metrics". Bleibt als Referenz in der Roadmap markiert.
   - **Meilenstein 3:** Große LaTeX-Gesamtdokumentation & Showcase (3D MantaAuv & 2D NACA, ParaView-Renderings, GCI-Gitterkonvergenz).
   - **Meilenstein 4:** FEM-Integration & Fluid-Struktur-Kopplung (CalculiX `ccx` als zweite `SolverStage`).

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
* [`.plans/.old/automatisches_ende.md`](.old/automatisches_ende.md) — AutoStop (`AutoStop`-Modus `"On"`, Fenster & Mindestverbesserung) umgesetzt.
* [`.plans/.old/wandschicht_zellzahl.md`](.old/wandschicht_zellzahl.md) — Feste Wandschicht-Schichtzahl (Stufen A/B/C), gemessene Zellgrenze (`MaxCells3D`), Netzkennwerte in CSV.

---

## 3. Aktueller Systemstatus

* **Physik & Vernetzung:** 2D-CFD (`TargetYPlus = 1.0`, `GrowthRate = 1.2`) und 3D-CFD (`TargetYPlus = 1.5`, `GrowthRate = 1.28`, `MeshSizeFromCurvature = true` mit `CurvatureMinSizeFactor = 0.5`), `scaleFactor`-konsistenter `GmshCfdMesher` in realen mm, physikalische Windkanal-Garantien ($\ge 5\,L / \ge 15\,L / \ge 5\,B/H / \le 0{,}3\,\%$ Blockage), `DetailBox` inkl. Z-Mindestpuffer (`DetailBoxMinHeightLengthFactor = 0.15`), adaptive GCI-Kalibrierung, Knickpunkt-Schutz (B14) sowie Start-Voxelprüfung (`VoxelResolutionAdvisor` / `AutoVoxelResolution`).
* **SU2-Konvergenz & Telemetrie:** `CauchyTolerance = "1e-5"`, automatischer Steady-Restart bei Rest-Drift (`AutoRestartOnDrift = true`, `RESTART_SOL= YES`), Druck-/Reibungs-Widerstandsanteile (`Su2_CD_p`, `Su2_CD_v`), Zellklassen (`MeshCells_*`) und reale Tunnelmaße in CSV/`calibration.csv`, sowie `DurationSeconds`-Erhalt beim `--resume`.
* **Pipeline & Algorithmen:** RSM, Evolutionäre Optimierung, **Bayes-Optimierung (Gauß-Prozess, EI, Metrics-Modus, Vertrauensbereich)**, robuster Champion-Validator mit relativem Jitter und Parameter-Bounds-Clamping (KP-2), aktive Anstellwinkel-Optimierung (KP-4), plattformunabhängiger `ExecutableResolver` (KP-7).
* **Konfiguration:** `FreestreamVelocity` (m/s) ist der Standardweg zur Anströmgeschwindigkeit; `MachNumber` bleibt nur als veralteter Rückfall (`FreestreamVelocity = 0`). Die Vorlagen und Projekt-JSONs (`MantaAuv`, `Naca2D`) sind auf genaue Rechnungen ausgelegt (Auto-Netzkalibrierung, strenge `ControlRuns`, Feinprüfung der besten Varianten, instationär-stabile Rechnungen gültig).
