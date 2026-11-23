# Phase 4 — Zusatzplan: Optionales Container-Setup (Docker / Apptainer)

> **Status:** Geplant (Umsetzung erst in Phase 4 / `AP 4.1`; noch **nicht** gecodet)  
> **Einordnung:** Ergänzendes Deployment-Angebot für externe Nutzer, neue Linux-Server, HPC-Cluster mit Apptainer und lokale Windows-/WSL2-Tests. Das bestehende rootless Setup ([`setup_server.sh`](../src/FluidForge/setup_server.sh)) bleibt dauerhaft als gleichberechtigter Fallback bestehen.

---

## 1. Geprüfter Server-Status auf `BIC_12` (SSH-Check vom 23.11.2026)

Vor einer Container-Entscheidung wurde der aktuelle Arbeitsserver `BIC_12` (`Bionik-E2-12`) direkt per SSH geprüft:

| Prüfpunkt | Ergebnis auf `BIC_12` | Bedeutung |
| :--- | :--- | :--- |
| **Betriebssystem / Kernel** | Ubuntu `24.04.5 LTS` (Noble), Linux `6.17.0-1033-oem x86_64` | Moderne Linux-Basis |
| **Installierte Container-Runtimes** | `apptainer`, `singularity`, `docker`, `podman`, `enroot`, `charliecloud`, `buildah`, `fuse-overlayfs` → **alle `NOT FOUND`** | Aktuell keine Container-Ausführung möglich |
| **Benutzerrechte / User-Namespaces** | Regulärer Nutzer (`uid=1750`, keine `docker`-Gruppe), `unprivileged_userns_clone=1`, aber **`apparmor_restrict_unprivileged_userns=1`** und **`/etc/subuid` / `/etc/subgid = none`** | Auch rootless Container (`podman`/unprivilegiertes `apptainer`) sind ohne Admin-Konfiguration (AppArmor-Profil + `subuid`/`subgid`) gesperrt |
| **Freier Speicher (`/`)** | `101 GB` frei (`337 / 461 GB`, `78 %` belegt) | Ausreichend für Simulationen, aber Images sollten kompakt bleiben |

**Konsequenz:**
- Auf **`BIC_12`** (und vergleichbaren gesperrten Uni-Servern ohne `sudo`) bleibt das bestehende Conda-/System-Setup ([`setup_server.sh`](../src/FluidForge/setup_server.sh) + System-`openfoam2506` + `mpich` + `opt-py`) **zwingend erforderlich** und der produktive Standard.
- Ein Container-Setup ist daher **kein Ersatz** für `setup_server.sh`, sondern ein **zusätzliches Angebot in Phase 4** für:
  1. externe Open-Source-Nutzer („Zero-to-First-Run“ in einem Befehl statt 200 Zeilen Build-Skript),
  2. lokale OpenFOAM-Läufe unter Windows (Docker Desktop / WSL2) und macOS/Linux,
  3. andere HPC-Cluster oder eigene Cloud-/Instituts-Server, auf denen `apptainer`/`singularity` oder `docker` installiert ist.

---

## 2. Zielarchitektur: Toolchain-Only-Container (Code per Bind-Mount)

**Grundprinzip:** Nur die **schwere, fehleranfällige Tool-Kette** kommt in das Image — der eigentliche `FluidForge`-Quellcode, Projektordner und `results/` bleiben auf dem Host und werden per Bind-Mount (`-v` / `--bind`) eingehängt.

- **Vorteil:** Änderungen an Projekten, Geometrien oder C#-Code erfordern **keinen Image-Neubau** (`dotnet build` / `sim.ps1` laufen weiter in Sekunden).
- **MPI-Sauberkeit:** Im Container sind exakt gepinnte, getestete Versionen sauber voneinander isoliert (`SU2 7.5.1` + MPICH-Wrapper, `OpenFOAM ESI v2506` + System-OpenMPI, `Gmsh 4.11.1`, vorkompilierte `libpicogk.so` + `xvfb-run`, `.NET 9.0 SDK`).

### Zwei Multi-Stage-Image-Targets (Image-Größe im Griff behalten)

| Target | Inhalt | Geschätzte Größe | Einsatzgebiet |
| :--- | :--- | :--- | :--- |
| **`fluidforge-toolchain:base`** *(Standard)* | Ubuntu 24.04 + `.NET 9.0` + `PicoGK` (headless, Xvfb/Mesa) + `Gmsh 4.11.1` + `SU2 7.5.1` (MPICH) + `OpenFOAM ESI v2506` (OpenMPI) | ~2,5–3,5 GB | Voller 2D-/3D-CFD-Betrieb mit eingebautem C#-`Bayes` (Zero-Python-Overhead) |
| **`fluidforge-toolchain:full`** *(Optional)* | Alles aus `base` + isoliertes Python-Env (`opt-py`: CPU-PyTorch, `BoTorch`, `GPyTorch`, `pymoo`, `matplotlib`, `pandas`) | ~4,0–5,5 GB | Pareto-Fronten (`qNEHVI`, `NSGA-II`), Multi-Fidelity-BO & direktes Plotting im Container |

---

## 3. Geplante Arbeitspakete (für Phase 4 — noch nicht umsetzen)

### AP 4-C1: `docker/Dockerfile` & Entrypoint erstellen
- [ ] Multi-Stage-`docker/Dockerfile` auf Basis `ubuntu:24.04` mit Targets `base` und `full` erstellen (alle Tool-Versionen strikt gepinnt wie in `setup_server.sh`).
- [ ] Saubere MPI-Trennung im Container sicherstellen: `SU2_CFD` ruft ausschließlich MPICH (`mpiexec`), `OpenFOAM` ruft ausschließlich System-OpenMPI (`/usr/bin/mpirun`) — konsistent mit `OpenFoamProcessRunner` und `sim-runner.sh`.
- [ ] `docker/entrypoint.sh` anlegen, das optional `xvfb-run -a` für `PicoGK`-Projekte kapselt und die Host-UID/GID sauber respektiert (keine `root`-Dateien in `results/`!).
- [ ] `.dockerignore` anlegen (`results/`, `server-results/`, `Serverergebnisse/`, `bin/`, `obj/`, `.git/` ausschließen).

### AP 4-C2: Apptainer-Kompatibilität & Nutzungs-Wrapper dokumentieren
- [ ] Sicherstellen, dass das Docker-Image ohne Änderungen per `apptainer pull fluidforge.sif docker://ghcr.io/<owner>/fluidforge-toolchain:base` auf HPC-Clustern lauffähig ist (kein Schreibzugriff im Container-Dateisystem nötig, `/tmp` & Workspace per `--bind`).
- [ ] Optionale `docker-compose.yml` (oder kurzes Wrapper-Skript `scripts/run-in-container.sh` / `.ps1`) für einzeilige Befehle:
  - `--print-config`, `--smoke-test`, `dotnet test`, 2D-SU2-Testlauf und 3D-OpenFOAM-Testlauf (`PipeBend90`).
- [ ] Kurze Anleitung in `docs/container-workflow.md` ergänzen und aus `README.md` sowie `docs/server-workflow.md` verlinken (inkl. klarer Entscheidungshilfe: *Wann Container, wann `setup_server.sh`?*).

### AP 4-C3: Verifikation & Optionales GHCR-Publish
- [ ] Lokal (Docker / WSL2) alle 4 Kern-Checks im Container verifizieren:
  1. `dotnet test --filter "Category!=Integration"` (alle Unit-Tests grün)
  2. `Naca2D` (`--smoke-test` + echter 2D-SU2-Integrationstest)
  3. `PipeBend90` (`--smoke-test` + echter `OpenFoamIntegrationTests` mit `mpirun -np 4`)
  4. `full`-Target: `Category=PythonOpt` (`BoTorch` + `pymoo` + `plot_pareto.py`)
- [ ] *(Optional beim GitHub-Release)* GitHub-Actions-Workflow zum automatischen Bauen und Veröffentlichen auf **GHCR** (`ghcr.io`), sodass externe Nutzer das Image nicht selbst kompilieren müssen.
- [ ] `THIRD_PARTY_NOTICES.md` prüfen: Bei Distribution eines fertigen Images mit OpenFOAM (GPL-3.0) + SU2 (LGPL-2.1) + Gmsh (GPL-2.0) + PicoGK (Apache-2.0) die jeweiligen Lizenz- und Source-Offer-Hinweise sauber im Image und im Repo mitführen.

---

## 4. Bewusste Abgrenzung (Was wir NICHT tun)

- **Keine Pflicht zum Container:** `scripts/sim.ps1`, `scripts/sim-runner.sh` und `setup_server.sh` funktionieren unverändert nativ ohne Container (wichtig für `BIC_12`!).
- **Kein Multi-Node-MPI-Overengineering:** Der Fokus liegt auf Single-Node-Workstations und Einzelknoten-Servern (4–64 Kerne). Multi-Node-InfiniBand-HPC-MPI bleibt außen vor, bis es konkret benötigt wird.
