# Phase 5 (Ausblick / Grobkonzept): LLM-gestützte Code-Evolution („Open-Source Noyron + AlphaEvolve“)

> **Status:** Bewusst noch **Grobkonzept / Ausblick** — wird erst konkret ausdetailliert, nachdem Phasen 1–4 abgeschlossen und durch Community-Feedback geschärft sind!  
> **Architektur-Vorbereitung:** Die nötigen Schnittstellen (CLI-Befehle, Smoke-Test, Ask/Tell-Protokoll, strukturierte Diagnostik, Hash-Schutz) werden bereits in Phasen 1–3 sauber angelegt, sodass Phase 5 ohne Kern-Umbau andocken kann.

---

## 1. Vision & Grundprinzip

Hier schließt sich der Kreis zu deiner ursprünglichen Inspiration (LEAP 71 / Noyron + AI-Code-Evolution à la DeepMind AlphaEvolve / OpenEvolve):
- **LEAP 71 / Noyron** zeigt die Macht von *Computational Engineering Models* (deterministischer C#-Code auf `PicoGK` + `ShapeKernel`, der komplexe Bauteile erzeugt).
- **Aktuelle LLM-CFD-Agents** (`Foam-Agent`, `OpenFOAMGPT`) scheitern in der Praxis oft am *„Reliability Gap“*: Sie erzeugen lauffähige, aber physikalisch falsche Simulationen oder betrügen die Zielfunktion (*Reward Hacking*).
- **Dein Alleinstellungsmerkmal nach Phase 4:** Du besitzt ein **hartes, deterministisches, physikalisch verifiziertes Optimierungs- und Prüfsystem**. Genau darüber sitzt in Phase 5 eine **äußere, eng geführte LLM-Code-Evolution-Schleife**:

```text
  Mensch (Entwurfsvorgabe, unveränderliche Objectives & Constraints, Freigabe)
       │
       ▼
  ┌─────────────────────────────────────────────────────────────────────┐
  │ Äußere Schleife (LLM-Agent / OpenEvolve-Prinzip)                    │
  │ • Liest letzten Generator-Code + strukturierte Physik-Diagnostik    │
  │ • Ändert STRUKTUR / TOPOLOGIE im ShapeKernel/PicoGK-Generator       │
  │   (z. B. neue Kanalverzweigung, Leitbleche, Querschnittsverlauf)    │
  └──────────────────────────────┬──────────────────────────────────────┘
                                 ▼
  ┌─────────────────────────────────────────────────────────────────────┐
  │ Harte deterministische Prüftore (dein Framework aus Phase 1–4)      │
  │  Tor 1: dotnet build & Unit-Tests                                   │
  │  Tor 2: --smoke-test (Wasserdichtheit, Patches, Wandstärke,         │
  │         "jeder Parameter wirkt")                                    │
  │  Tor 3: SHA-256-Hash-Prüfung (Objectives & Constraints unverändert  │
  │         → kein Reward Hacking möglich!)                             │
  └──────────────────────────────┬──────────────────────────────────────┘
                                 ▼
  ┌─────────────────────────────────────────────────────────────────────┐
  │ Innere Schleife (dein Framework: Bayes / BoTorch / OpenFOAM / SU2)  │
  │ • Optimiert die NUMERISCHEN Parameter der neuen Topologie           │
  │ • Verifiziert mit checkMesh / Massenbilanz / y+ / GCI / Bouncer     │
  │ • Exportiert strukturierte Diagnostik zurück ans LLM                │
  └─────────────────────────────────────────────────────────────────────┘
```

### Klare Arbeitsteilung (was wir schon aus der früheren `llm_geometrie_bewertung.md` wissen)
| Aufgabe | Zuständig | Warum |
| :--- | :--- | :--- |
| **Topologie & Code-Struktur erfinden/umbauen** | **LLM-Agent** | Programmieren neuer geometrischer Konzepte (z. B. neue Rippen, Verzweigungsarten, Querschnittsgesetze) — genau dort stößt feste Parametrisierung an ihre Grenze. |
| **Zahlen/Parameter feinoptimieren** | **Bayes / BoTorch / pymoo** | Schneller, reproduzierbar, mit Unsicherheitsmaß und Pareto-Front — ein LLM wäre beim Zahlendrehen teurer und schlechter. |
| **Physik berechnen & prüfen** | **OpenFOAM / SU2 + Verifikations-Harness** | Das LLM *rät keine Physik*, sondern reagiert auf gemessene physikalische Ursachen! |
| **Ziele, Constraints & Freigabe** | **Mensch** | Hash-gesicherte Objectives & Constraints verhindern Reward Hacking. |

---

## 2. Was Phasen 1–3 architektonisch bereits dafür vorbereiten (ohne jetzt LLM-Code zu bauen)

1. **Saubere CLI- & Maschinen-Schnittstelle (später auch als MCP-Server kapselbar):**
   - `--smoke-test <Project>` (Tor 2: prüft Geometrie, Patches und Parameter-Sensitivität in Sekunden)
   - `--eval-once <Project>` (einzelne definierte Variante testen)
   - `--run <Project>` / `--diagnose <Project>` (liefert kompaktes JSON-Diagnostik-Paket)
2. **Strukturierte Physik-Diagnostik als „Physics-Informed Feedback“:**
   - Nicht nur eine Fitness-Zahl zurückgeben, sondern maschinenlesbares JSON mit:
     - Druck- vs. Reibungsanteil (`CD_p` vs. `CD_v` bzw. Druckverlust-Anteil je Zone),
     - Constraint-Margins & Fehlerursachen (wo bricht Volumen/Wandstärke/Massenbilanz?),
     - ARD-Parameter-Wichtigkeit (welche Parameter sind „tot“ und können weg?),
     - `BoundaryHint` (welche Parameter kleben an Grenzen?),
     - Pareto-Fortschritt gegenüber Vorgänger-Generation (`<Project>_E01` vs. `<Project>_E02`).
3. **Projektordner-Isolation (`<Project>_E01`, `_E02`, …):**
   - Jede LLM-Design-Generation wird ein sauberer, per `dotnet build` geprüfter Ordner (Weg A statt unsicherer Runtime-Roslyn-Kompilierung) inkl. `Quellstand/`-Snapshot und unveränderlichem Hash auf die Bewertungsdateien.

---

## 3. Grobe Umsetzungs-Bausteine (Ausarbeitung nach Phase 4)

- [ ] **AP 5.1: Maschinenlesbare Diagnostik- & MCP/CLI-Fassade** über dem fertigen Framework aus Phase 4 aufsetzen (`diagnose.json` + optionaler schlanker MCP-Server).
- [ ] **AP 5.2: SHA-Hash-Prüftor & Evolutions-Orchestrator (Human-in-the-Loop):** Kopiert den besten Projektordner nach `<Name>_E{k+1}`, lässt das LLM nur den `ShapeKernel`-Generator + Parameterdefinition ändern, erzwingt Tor 1–3, zeigt dem Menschen den Diff zur Freigabe und startet danach eine budgetierte innere Optimierungsrunde (Multi-Fidelity / Bayes).
- [ ] **AP 5.3: Optionaler Anschluss an Open-Source Code-Evolution (`OpenEvolve` / MAP-Elites):** Falls die halb-automatische Schleife überzeugende Topologie-Sprünge liefert, Anbindung an eine evolutionäre Insel-/Archiv-Strategie über mehrere Design-Familien.
- [ ] **AP 5.4: Großer Validierungslauf & zweiter wissenschaftlicher Bericht:** Fairer Vergleich unter identischem Rechenbudget:
  1. Handgezeichnete Baseline-Geometrie,
  2. Parametrische Optimierung (Phase 1–4, feste Topologie),
  3. LLM-Code-Evolution + parametrische Optimierung (Phase 5, freie Topologie) — idealerweise mit 3D-Druck & realer Messung!

---

## 4. Offene Fragen (bewusst erst nach Phase 4 zu klären)
- Welches konkrete Bauteil zeigt den größten Unterschied zwischen fester Parametrisierung und freier Topologie-Evolution?
- Welches LLM-Backend (API vs. lokales Open-Weights-Coding-Modell vs. Claude-Code/Antigravity-Skill) liefert die stabilsten `ShapeKernel`-Generatoren?
- Wie viel visuelle Rückmeldung (automatische ParaView-Schnitte / Druckverlust-Heatmaps für ein multimodales Vision-LLM) verbessert die Topologie-Ideen messbar?
