# Projektpläne & Roadmap (Übersicht für KI-Sessions)

> **Stand: 07.11.2026** — Status vor dem Merge des Branches `MajorPhysikFixes` in `main`.
> **Test- & Buildstatus:** 557/557 Unit-Tests grün (`dotnet test`), 0 Warnungen, 0 Fehler. BIC_12 Server-Deploy erfolgreich.

---

## 1. Abgeschlossene & Historische Pläne (Nicht erneut vollständig einlesen!)
*Zur Schonung des Kontextfensters/Token-Verbrauchs müssen folgende Dateien in neuen Sessions **nicht** mehr komplett gelesen werden:*
* `architektur_refactoring.md` — Entscheidungen 1–8 & TODO-1 bis 52 (Architektur, Multi-Stage, Journaling) vollständig umgesetzt.
* `projektbuendel.md` — Multi-Projekt-Struktur (`src/projects/`), Vorlagen (`_Vorlage`, `_Vorlage2D`) und Skripte etabliert.
* `anmerkungen_umsetzung.md` — Frühe Anmerkungen & Feedbackpunkte abgearbeitet.
* `architektur_kritikpunkte_naca.md` — KP-1 bis KP-7 vollständig behoben (RubberBandScaler, ChampionValidator, AoA, Resolver).
* `physik_feinschliff.md` — Block W (TODO-W1 bis W10: adaptive Kalibrierung, Detailbox, URANS-Restart) vollständig abgeschlossen.
* `servertest_2026-11-06.md` — Servertest Ketten 1–3 auf BIC_12 ausgewertet; alle Folgefehler B1–B17 vollständig behoben.

---

## 2. Aktueller Status des Frameworks
* **Physik & Vernetzung:** 2D- und 3D-CFD (SU2) mit Wandauflösung ($y^+ \approx 1$), adaptiver Kalibrierung, Detailbox und Knickpunkt-Schutz an scharfen Hinterkanten (B14).
* **Pipeline & Algorithmen:** RSM, Evolutionäre Optimierung, robuster Champion-Validator mit relativem Jitter und Parameter-Bounds-Clamping (KP-2), aktive Anstellwinkel-Optimierung (KP-4), plattformunabhängiger `ExecutableResolver` (KP-7).
* **Konfiguration:** `MachNumber` vollständig durch `FreestreamVelocity` abgelöst; saubere Projekt-JSONs ohne störende Warnungen.

---

## 3. Nächste Schritte laut Roadmap (`Anmerkungen.txt` Z. 56–61)
1. **Merge nach `main`:** Branch `MajorPhysikFixes` sauber in `main` mergen (stabiles Physik- & Architektur-Fundament).
2. **2D-Testlauf (`Naca2D`):** Kleiner 5×5-Testlauf in 2D mit aktiver Auto-Kalibrierung und neuer Detailbox; Kurzbericht mit Plots.
3. **KI-Integration (`ki_optimierung.md` Block G–I, TODO-53 bis 60):**
   - TODO-53: Bouncer-Markierung `Disqualified = true` & separates Journaling.
   - TODO-54 bis 57: Gaussian Process / BO (Bayesian Optimization) als alternativer Algorithmus (`Algorithm = "Bayes"`).
   - TODO-58 bis 60: Reproduzierbarkeit, Seeds und Validierungslauf 5×5 im Vergleich zu RSM/EA.
4. **Auswertungs-Tooling & Dokumentation:**
   - Python-Auswertevorlagen unter `scripts/` (Residuen, Cauchy-Konvergenz, Fitness-Verlauf, SVG/Netz-Darstellung).
   - Ausführliche LaTeX-Gesamtdokumentation (3D MantaAuv + 2D NACA) mit Methodenvergleich und Kalibrierungsnachweisen.
5. **Ausblick:** FEM-Strukturmechanik-Integration (`fem_ausblick.md`).
