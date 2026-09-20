#!/usr/bin/env bash
#
# Server-Seite der Fernsteuerung: baut, startet, ueberwacht und stoppt den
# Optimierungslauf auf dem Linux-Rechner (ohne Sudo).
#
# Wird normalerweise von scripts/sim.ps1 (Windows) per SSH aufgerufen, laesst sich
# aber genauso von Hand benutzen, wenn man ohnehin per SSH eingeloggt ist:
#
#     cd ~/Documents/AutomatisierungCleanVersion
#     bash scripts/sim-runner.sh status
#
# Fasst die Handgriffe aus ServerHochschuleEinrichten2.txt (Teil 2 bis Teil 6)
# zusammen. Das Erst-Setup des Servers macht weiterhin setup_server.sh --
# dieses Skript setzt voraus, dass Miniconda, SU2, Gmsh, PicoGK und .NET liegen.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"

SESSION="${SIM_SESSION:-sim}"

# KEIN stiller Standard mehr (TODO-28). Hier stand
#
#     PROJECT_NAME="${SIM_PROJECT:-MantaAuv}"
#
# und damit haette jeder Vertipper in SIM_PROJECT -- oder eine Variable, die den Weg in
# die tmux-Sitzung nicht findet -- klaglos MantaAuv gerechnet. Stundenlang, mit plausibel
# aussehenden Zahlen, die in Ergebnisse/MantaAuv/ landen und dort die CSV eines echten
# Laufs ueberschreiben. Program.cs hat denselben Grundsatz seit TODO-23.
#
# Welche Befehle ohne Projekt keinen Sinn haben, entscheidet require_project. stop, log,
# running und hash brauchen keines: sie arbeiten auf dem Prozess bzw. auf Dateien, die
# es nur einmal gibt.
PROJECT_NAME="${SIM_PROJECT:-}"

APP_PROJECT="$REPO_DIR/src/Automatisierung_v2/Automatisierung_v2.csproj"
BIN_DIR="$REPO_DIR/src/Automatisierung_v2/bin/Debug/net9.0"
APP_DLL="$BIN_DIR/Automatisierung_v2.dll"

# Seit TODO-24 liegt alles Einstellbare im Projektordner. config/ enthaelt nur noch
# die README und ggf. eine maschinenspezifische *.local.json -- es ist kein
# Pflichtverzeichnis mehr und fehlt auf einem frischen Rechner voellig.
PROJECTS_DIR="$REPO_DIR/src/projects"
CONFIG_DIR="$REPO_DIR/config"

LOG_FILE="$REPO_DIR/simulation.log"
LOG_ARCHIVE="$REPO_DIR/logs"
HASH_FILE="$REPO_DIR/.deploy_hash"
EXIT_FILE="$REPO_DIR/.last_exit"

# Alles Projektbezogene setzt require_project, nicht der Dateikopf: ohne Projektnamen
# waeren das Pfade wie ".../Ergebnisse/" -- und die zeigen auf etwas, das existiert.
# Ergebnisse liegen seit TODO-25 je Projekt getrennt (zwei Projekte ueberschrieben sich
# vorher gegenseitig die CSV).
PROJECT_DIR=""
RESULT_DIR=""
RESULT_CSV=""

PICOGK_BUILD_DIR="${PICOGK_BUILD_DIR:-$HOME/software/PicoGK/build}"

# Das Prozessmuster, an dem wir unseren eigenen Lauf erkennen. Bewusst die DLL und
# nicht "dotnet": ein pkill auf "dotnet" wuerde jeden anderen .NET-Prozess des
# Nutzers mit abschiessen.
PROCESS_PATTERN="Automatisierung_v2.dll"

# ---------------------------------------------------------------------------
# Umgebung
# ---------------------------------------------------------------------------
# WICHTIG: Bei "ssh host befehl" startet bash nicht-interaktiv, und Ubuntus
# ~/.bashrc steigt in diesem Fall in den ersten Zeilen wieder aus. Nichts aus
# conda init oder den MESA-Zeilen in ~/.bashrc ist hier also gesetzt -- jeder
# Aufruf muss sich seine Umgebung selbst bauen (entspricht Teil 2 der Anleitung).
load_env() {
    export PATH="$HOME/miniconda/bin:$HOME/.dotnet:$HOME/software/bin:$HOME/software/gmsh-4.11.1-Linux64/bin:$PATH"
    export LD_LIBRARY_PATH="$HOME/miniconda/lib:${LD_LIBRARY_PATH:-}"

    # Unterdrueckt die hwloc-Warnungen von OpenMPI auf virtualisierten Maschinen
    export HWLOC_HIDE_ERRORS=1

    # PicoGK zieht OpenGL hoch, auch im Headless-Betrieb: Software-Rendering
    export LIBGL_ALWAYS_SOFTWARE=1
    export MESA_GL_VERSION_OVERRIDE=4.1
    export MESA_GLSL_VERSION_OVERRIDE=410

    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1
}

# ---------------------------------------------------------------------------
# Hilfsfunktionen
# ---------------------------------------------------------------------------
say()  { printf '%s\n' "$*"; }
ok()   { printf '  [ OK ]  %s\n' "$*"; }
fail() { printf '  [FEHLT] %s\n' "$*"; }

is_running() {
    pgrep -u "$USER" -f "$PROCESS_PATTERN" >/dev/null 2>&1
}

running_pid() {
    pgrep -u "$USER" -f "$PROCESS_PATTERN" | head -n 1
}

has_session() {
    tmux has-session -t "$SESSION" >/dev/null 2>&1
}

# Die Projektordner, wie das Programm sie auch sieht: ohne die Vorlage und ohne
# Build-Artefakte -- dasselbe Kriterium wie ProjectPaths.ListProjectNames (TODO-27).
# Die Liste stammt aus dem Dateisystem und kommt daher auch ohne gebaute DLL zustande.
list_project_folders() {
    [ -d "$PROJECTS_DIR" ] || return 0

    find "$PROJECTS_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' 2>/dev/null \
        | grep -Ev '^(_Vorlage|bin|obj|\..*)$' | sort || true
}

# Bricht ab, wenn kein Projekt angegeben wurde, und leitet erst danach die
# projektbezogenen Pfade ab (TODO-28).
#
# Der Parameter ist der Befehlsname DIESES Skripts fuer den Beispielaufruf -- wer
# 'status' aufgerufen hat, soll 'status' im Beispiel wiederfinden. Die Windows-Zeile
# nennt bewusst keinen Befehl: sim.ps1 heisst an einer Stelle anders (run statt start).
require_project() {
    local command_name="$1"

    if [ -z "$PROJECT_NAME" ]; then
        say "[RUNNER] FEHLER: Kein Projekt angegeben, und es gibt keine Vorgabe mehr."
        say "         Von Windows aus:  .\\scripts\\sim.ps1 <befehl> -Project <Name>"
        say "         Von Hand:         SIM_PROJECT=<Name> bash scripts/sim-runner.sh $command_name"

        local folders
        folders="$(list_project_folders | tr '\n' ' ')"
        if [ -n "$folders" ]; then
            say "         Vorhanden sind:   $folders"
        else
            say "         Unter src/projects/ liegt allerdings kein Projekt."
        fi

        exit 2
    fi

    # Der Name wird unten in die Kommandozeile der tmux-Sitzung eingesetzt und ist
    # ausserdem Ordner- UND Klassenname (Entscheidung 1, TODO-27). Was kein Bezeichner
    # ist, kann also nur ein Tippfehler sein -- und hat in einer Kommandozeile nichts
    # verloren.
    case "$PROJECT_NAME" in
        *[!A-Za-z0-9_]* | [!A-Za-z]*)
            say "[RUNNER] FEHLER: '$PROJECT_NAME' ist kein gueltiger Projektname."
            say "         Erlaubt sind ein Buchstabe am Anfang, danach Buchstaben, Ziffern"
            say "         und Unterstrich -- der Name ist auch Ordner- und Klassenname."
            exit 2
            ;;
    esac

    PROJECT_DIR="$PROJECTS_DIR/$PROJECT_NAME"
    RESULT_DIR="$REPO_DIR/Ergebnisse/$PROJECT_NAME"
    RESULT_CSV="$RESULT_DIR/Simulation_Results.csv"
}

# Fragt das Programm EINMAL nach der wirksamen Konfiguration und merkt sich die Ausgabe.
#
# Vorher fischte hier ein sed-Einzeiler in config/simulation.json. Das ging, solange es
# genau eine Datei gab. Seit TODO-22/24 sind es bis zu vier Schichten ueber mehrere
# Verzeichnisse -- doctor haette also Pfade geprueft, die im Lauf gar nicht gelten.
# "--print-config" gibt SCHLUESSEL=WERT aus, eine Zeile je Eintrag, und laeuft vor dem
# Kernel-Start (also ohne PicoGK und ohne Xvfb).
#
# WICHTIG: "das Programm konnte nicht gefragt werden" und "der Schluessel ist nicht
# konfiguriert" sind zwei verschiedene Befunde. Die erste Fassung hat beides zu
# "steht nicht in der Konfiguration" verschmolzen und die Ausgabe mit 2>/dev/null
# verworfen -- ein veralteter Build sah damit aus wie eine kaputte Konfiguration.
# Das Programm meldet Bedienfehler ausserdem auf stdout, nicht auf stderr.
CONFIG_DUMP=""
CONFIG_ERROR=""

load_effective_config() {
    CONFIG_DUMP=""
    CONFIG_ERROR=""

    if [ ! -f "$APP_DLL" ]; then
        CONFIG_ERROR="das Programm ist noch nicht gebaut"
        return 0
    fi

    if ! CONFIG_DUMP="$(dotnet "$APP_DLL" "$PROJECT_NAME" --print-config 2>&1)"; then
        CONFIG_ERROR="das Programm hat mit einem Fehler abgebrochen"
        return 0
    fi

    # Der Aufruf lief durch, lieferte aber keine SCHLUESSEL=WERT-Zeilen. Haeufigster
    # Grund: die DLL auf dem Server ist aelter als "--print-config" und hat das
    # Argument als Konfigurationsverzeichnis gedeutet.
    if ! printf '%s\n' "$CONFIG_DUMP" | grep -q '^MpiRunPath='; then
        CONFIG_ERROR="das Programm kennt '--print-config' nicht (veralteter Build?)"
    fi
}

config_value() {
    printf '%s\n' "$CONFIG_DUMP" | sed -n "s/^$1=//p" | head -n 1
}

# Ist das ein ausfuehrbares Programm -- als Pfad oder ueber den PATH? Seit TODO-24
# stehen in der Konfiguration bloss noch "mpirun", "SU2_CFD" und "gmsh"; load_env legt
# die passenden Verzeichnisse auf den PATH, und .NET loest den Namen darueber auf.
resolve_tool() {
    local candidate="$1"
    [ -n "$candidate" ] || return 1

    if [ -x "$candidate" ]; then
        printf '%s' "$candidate"
        return 0
    fi

    command -v "$candidate" 2>/dev/null || return 1
}

# libpicogk.so neben die DLL legen. Muss nach JEDEM dotnet build passieren --
# der Build raeumt bin/Debug/net9.0 gelegentlich auf und nimmt die C++-Bibliothek
# mit (der Hinweis steht so auch in Teil 3 der Anleitung).
restore_picogk() {
    mkdir -p "$BIN_DIR"

    if [ -f "$BIN_DIR/libpicogk.so" ] && [ -f "$BIN_DIR/libpicogk.26.2.so" ]; then
        return 0
    fi

    local lib
    lib="$(find "$PICOGK_BUILD_DIR" -iname '*picogk.so' 2>/dev/null | head -n 1 || true)"

    if [ -z "$lib" ]; then
        say "[WARNUNG] Keine kompilierte PicoGK-Bibliothek unter $PICOGK_BUILD_DIR gefunden."
        say "          Ohne libpicogk.so startet der Lauf nicht -- setup_server.sh ausfuehren."
        return 1
    fi

    # Beide Namen, wie in setup_server.sh: das NuGet-Paket sucht die versionierte Datei.
    cp -f "$lib" "$BIN_DIR/libpicogk.so"
    cp -f "$lib" "$BIN_DIR/libpicogk.26.2.so"
    say "[RUNNER] libpicogk.so aufgefrischt (Quelle: $lib)"
}

# ---------------------------------------------------------------------------
# Befehle
# ---------------------------------------------------------------------------

# Prueft alles, was der Lauf braucht, BEVOR man ihn in tmux wegschickt --
# die "Goldene Regel" aus der Anleitung, nur vollstaendiger.
cmd_doctor() {
    require_project doctor
    load_env
    local problems=0

    say "=== Umgebung auf $(hostname) ==="
    say "Repo:    $REPO_DIR"
    say "Projekt: $PROJECT_NAME"
    say ""

    say "--- Werkzeuge ---"
    local tool
    for tool in dotnet tmux xvfb-run; do
        if command -v "$tool" >/dev/null 2>&1; then
            ok "$tool -> $(command -v "$tool")"
        else
            fail "$tool ist nicht im PATH"
            problems=$((problems + 1))
        fi
    done

    say ""
    say "--- Programmpfade (wirksame Konfiguration des Projekts) ---"

    load_effective_config

    if [ -n "$CONFIG_ERROR" ]; then
        fail "Die wirksame Konfiguration laesst sich nicht abfragen: $CONFIG_ERROR"
        say  "          Aufruf war:  dotnet <DLL> $PROJECT_NAME --print-config"

        if [ -n "$CONFIG_DUMP" ]; then
            say "          Was das Programm gesagt hat:"
            printf '%s\n' "$CONFIG_DUMP" | tail -n 15 | while IFS= read -r line; do
                say "            $line"
            done
        fi

        say "          Das ist KEIN Pfadproblem -- die Pfade wurden gar nicht geprueft."
        say "          Meist hilft ein frischer Build:  .\\scripts\\sim.ps1 build"
        problems=$((problems + 1))
    else
        local entry
        for entry in MpiRunPath Su2Path GmshPath; do
            local configured resolved
            configured="$(config_value "$entry")"

            if [ -z "$configured" ]; then
                fail "$entry steht nicht in der Konfiguration"
                problems=$((problems + 1))
                continue
            fi

            if resolved="$(resolve_tool "$configured")"; then
                if [ "$resolved" = "$configured" ]; then
                    ok "$entry -> $resolved"
                else
                    ok "$entry -> $configured (ueber PATH: $resolved)"
                fi
            else
                fail "$entry -> $configured (weder Datei noch im PATH)"
                case "$configured" in
                    */*) say "          Der Pfad existiert nicht oder ist nicht ausfuehrbar." ;;
                    *)   say "          Gesucht im PATH: $PATH" ;;
                esac
                say "          Eintragen in src/projects/$PROJECT_NAME/simulation.json unter '$entry'."
                problems=$((problems + 1))
            fi
        done
    fi

    say ""
    say "--- Projekt ---"
    if [ -f "$APP_PROJECT" ]; then ok "csproj vorhanden"; else fail "csproj fehlt: $APP_PROJECT"; problems=$((problems + 1)); fi

    # config/ ist seit TODO-24 KEIN Pflichtverzeichnis mehr: es enthaelt nur noch den
    # Notausgang, und auf einem frischen Rechner fehlt es schlicht. Was zaehlt, ist der
    # Projektordner -- ohne ihn findet der Lauf seine Zahlen nicht.
    if [ -d "$PROJECT_DIR" ]; then
        ok "Projektordner vorhanden: src/projects/$PROJECT_NAME"
    else
        fail "Projektordner fehlt: $PROJECT_DIR"
        say "          Vorhanden sind: $(list_project_folders | tr '\n' ' ')"
        say "          Gross-/Kleinschreibung zaehlt hier: Linux unterscheidet sie."
        problems=$((problems + 1))
    fi

    # Eine vorhandene *.local.json ist kein Fehler, aber ein Sonderfall: sie ueberlebt
    # jedes Deploy und haelt damit Werte fest, die niemand hochgeladen hat.
    local local_files
    local_files="$(find "$CONFIG_DIR" "$PROJECTS_DIR" -name '*.local.json' 2>/dev/null || true)"
    if [ -n "$local_files" ]; then
        say "  [ACHTUNG] Maschinenspezifische Dateien gefunden:"
        printf '%s\n' "$local_files" | while IFS= read -r found; do say "            $found"; done
        say "            Normalerweise sollte es keine geben -- siehe config/README.md."
    fi

    if [ -f "$APP_DLL" ]; then ok "gebaute DLL vorhanden"; else say "  [INFO]  noch nicht gebaut (sim-runner.sh build)"; fi

    if [ -f "$BIN_DIR/libpicogk.so" ]; then
        ok "libpicogk.so liegt neben der DLL"
    elif [ -n "$(find "$PICOGK_BUILD_DIR" -iname '*picogk.so' 2>/dev/null | head -n 1 || true)" ]; then
        say "  [INFO]  libpicogk.so noch nicht kopiert -- macht der build-Schritt"
    else
        fail "libpicogk.so weder im bin-Ordner noch unter $PICOGK_BUILD_DIR"
        problems=$((problems + 1))
    fi

    say ""
    if [ "$problems" -eq 0 ]; then
        say "VALIDIERUNG ERFOLGREICH -- der Lauf kann gestartet werden."
        return 0
    fi

    say "VALIDIERUNG FEHLGESCHLAGEN: $problems Problem(e). Lauf NICHT starten."
    return 1
}

# Bewusst OHNE require_project: gebaut wird das Framework, und das ist fuer alle
# Projekte dasselbe. Ein Projekt ist nur ein Ordner mit C#-Dateien, den die csproj
# ueber src/projects/**/*.cs mit hineinzieht (TODO-24).
cmd_build() {
    load_env
    say "[RUNNER] Baue das Programm ..."
    dotnet build "$APP_PROJECT" -c Debug --nologo
    restore_picogk
    say "[RUNNER] Build fertig."
}

# Laeuft INNERHALB der tmux-Sitzung. Nicht von Hand aufrufen.
cmd_foreground() {
    # Auch hier die Pruefung, nicht nur in cmd_start: dieser Aufruf ist ein eigener
    # bash-Prozess, den der tmux-Server startet -- er erbt die Umgebung von cmd_start
    # nicht automatisch. cmd_start setzt SIM_PROJECT deshalb ausdruecklich davor.
    require_project start
    load_env
    cd "$REPO_DIR"

    {
        echo "==================================================================="
        echo " Start:    $(date '+%Y-%m-%d %H:%M:%S')"
        echo " Rechner:  $(hostname)"
        echo " Repo:     $REPO_DIR"
        echo " Projekt:  $PROJECT_NAME"
        echo " Stand:    $(cat "$HASH_FILE" 2>/dev/null || echo 'unbekannt')"
        echo "==================================================================="
    } >>"$LOG_FILE"

    local rc=0
    # xvfb-run -a: sucht sich selbst eine freie Display-Nummer, statt auf :99 zu
    # beharren und an einem alten, haengenden Xvfb zu scheitern.
    # Nur noch der Projektname. Das zweite Argument war frueher das config-Verzeichnis;
    # seit TODO-24 waere es das PROJEKT-Verzeichnis, und das findet das Programm selbst,
    # indem es von hier aus nach src/projects/ aufwaerts sucht.
    xvfb-run -a dotnet "$APP_DLL" "$PROJECT_NAME" >>"$LOG_FILE" 2>&1 || rc=$?

    echo "$rc" >"$EXIT_FILE"
    {
        echo "==================================================================="
        echo " Ende:     $(date '+%Y-%m-%d %H:%M:%S')  (Exit-Code $rc)"
        echo "==================================================================="
    } >>"$LOG_FILE"

    return "$rc"
}

cmd_start() {
    require_project start
    load_env

    if is_running; then
        say "[RUNNER] Es laeuft bereits ein Lauf (PID $(running_pid)). Erst 'stop' aufrufen."
        return 1
    fi

    if [ ! -f "$APP_DLL" ]; then
        say "[RUNNER] Noch nicht gebaut -- baue zuerst."
        cmd_build
    else
        restore_picogk || true
    fi

    # Alte, verwaiste Sitzung wegraeumen (z.B. nach einem Absturz)
    if has_session; then
        tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true
    fi

    # Vorheriges Log archivieren statt ueberschreiben -- sonst ist nach einem
    # Neustart nicht mehr nachvollziehbar, woran der letzte Lauf gescheitert ist.
    if [ -s "$LOG_FILE" ]; then
        mkdir -p "$LOG_ARCHIVE"
        mv "$LOG_FILE" "$LOG_ARCHIVE/simulation_$(date '+%Y%m%d_%H%M%S').log"
    fi
    : >"$LOG_FILE"
    rm -f "$EXIT_FILE"

    # SIM_PROJECT ausdruecklich davorgesetzt und nicht der Vererbung ueberlassen: die
    # Kommandozeile startet der tmux-SERVER, und dessen Umgebung stammt von dem Aufruf,
    # der ihn irgendwann einmal hochgezogen hat. Solange es einen stillen Standard gab,
    # fiel das nicht auf -- es wurde dann eben MantaAuv gerechnet (TODO-28).
    # require_project hat den Namen vorher geprueft, das Anfuehrungszeichen haelt.
    tmux new-session -d -s "$SESSION" \
        "SIM_PROJECT='$PROJECT_NAME' bash '$SCRIPT_DIR/sim-runner.sh' _foreground"

    # Kurz warten und nachsehen, ob der Prozess wirklich hochkommt. Ein Start,
    # der nach zwei Sekunden schon wieder tot ist, soll hier auffallen und nicht
    # erst, wenn jemand Stunden spaeter ins Log schaut.
    sleep 3

    if is_running; then
        say "[RUNNER] Gestartet in tmux-Sitzung '$SESSION' (PID $(running_pid))."
        return 0
    fi

    say "[RUNNER] FEHLER: Der Lauf ist sofort wieder beendet. Letzte Logzeilen:"
    tail -n 25 "$LOG_FILE" 2>/dev/null || true
    return 1
}

cmd_stop() {
    load_env

    if ! is_running && ! has_session; then
        say "[RUNNER] Es laeuft nichts."
        return 0
    fi

    say "[RUNNER] Stoppe Lauf ..."
    tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true

    # Panic-Button aus Teil 6 der Anleitung, aber auf den eigenen Nutzer begrenzt.
    # SU2_CFD und mpirun sind Kindprozesse und ueberleben das Ende der Sitzung
    # sonst -- sie wuerden weiter alle Kerne belegen.
    #
    # ACHTUNG: laeuft parallel noch ein zweiter Lauf (z.B. die alte Version in
    # ~/Documents/Automatisierung_v2 fuer den main-Vergleich), trifft es dessen
    # SU2- und mpirun-Prozesse mit. Das ist der Preis dafuer, keine verwaisten
    # Rechenprozesse zurueckzulassen.
    pkill -9 -u "$USER" -f "$PROCESS_PATTERN" >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f SU2_CFD          >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f mpirun           >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f xvfb-run         >/dev/null 2>&1 || true

    # Ein verwaister Xvfb wird bewusst NICHT abgeschossen: "xvfb-run -a" sucht sich
    # ohnehin eine freie Display-Nummer, ein Ueberbleibsel stoert also nicht.
    # Wenn doch aufgeraeumt werden soll:  pkill -9 -f Xvfb

    sleep 1

    if is_running; then
        say "[RUNNER] WARNUNG: Es laeuft immer noch etwas (PID $(running_pid))."
        return 1
    fi

    say "[RUNNER] Gestoppt."
}

cmd_status() {
    require_project status
    load_env

    say "=== Status auf $(hostname) ==="
    say "Repo:    $REPO_DIR"
    say "Projekt: $PROJECT_NAME"
    say "Stand:   $(cat "$HASH_FILE" 2>/dev/null || echo 'unbekannt')"

    if is_running; then
        local pid
        pid="$(running_pid)"
        say "Lauf:  LAEUFT (PID $pid, seit $(ps -o etime= -p "$pid" 2>/dev/null | tr -d ' '))"
    else
        if [ -f "$EXIT_FILE" ]; then
            say "Lauf:  gestoppt (letzter Exit-Code: $(cat "$EXIT_FILE"))"
        else
            say "Lauf:  gestoppt"
        fi
    fi

    if has_session; then say "tmux:  Sitzung '$SESSION' vorhanden"; else say "tmux:  keine Sitzung '$SESSION'"; fi

    say "Ergebnisse: Ergebnisse/$PROJECT_NAME/"

    if [ -f "$RESULT_CSV" ]; then
        local records
        records=$(($(wc -l <"$RESULT_CSV") - 1))
        say "CSV:   $records Datensatz/Datensaetze"
    else
        say "CSV:   noch keine Ergebnisse"
    fi

    if [ -f "$LOG_FILE" ]; then
        say ""
        say "--- letzte Logzeilen ---"
        tail -n "${1:-12}" "$LOG_FILE"
    fi
}

cmd_log() {
    local lines="${1:-40}" follow="${2:-yes}"
    if [ ! -f "$LOG_FILE" ]; then
        say "[RUNNER] Noch kein Log vorhanden ($LOG_FILE)."
        return 1
    fi
    if [ "$follow" = "yes" ]; then
        tail -n "$lines" -f "$LOG_FILE"
    else
        tail -n "$lines" "$LOG_FILE"
    fi
}

cmd_results() {
    require_project results

    if [ ! -f "$RESULT_CSV" ]; then
        say "[RUNNER] Noch keine Ergebnisse ($RESULT_CSV)."
        return 1
    fi
    head -n 1 "$RESULT_CSV"
    tail -n "${1:-10}" "$RESULT_CSV"
}

# Was dieser Server kennt (TODO-28). Bewusst OHNE require_project: das ist der Befehl,
# den man gerade dann aufruft, wenn man den Projektnamen NICHT weiss.
cmd_projects() {
    load_env

    say "=== Projekte auf $(hostname) ==="
    say "Repo: $REPO_DIR"
    say ""

    # Die massgebliche Liste kommt aus dem Programm: es findet die IProjectDefinition per
    # Reflection und nennt zusaetzlich Ordner, zu denen keine gehoert -- die sind nicht
    # startbar und genau das ist beim Anlegen eines Projekts der haeufigste Fehler.
    # --list-projects braucht selbst kein Projekt und startet keinen Kernel (TODO-23).
    if [ -f "$APP_DLL" ]; then
        dotnet "$APP_DLL" --list-projects
    else
        say "[INFO] Noch nicht gebaut. Welche Projekte STARTBAR sind, kann nur das"
        say "       Programm sagen -- hier also bloss die Ordner unter src/projects/:"

        local folders
        folders="$(list_project_folders)"

        if [ -n "$folders" ]; then
            printf '%s\n' "$folders" | while IFS= read -r name; do say "  $name"; done
        else
            say "  (keine)"
        fi
    fi

    # Die zweite Haelfte der Frage: wo wurde schon gerechnet? Ein Ergebnisordner kann auch
    # von einem Projekt stammen, das es im Code laengst nicht mehr gibt -- ein Deploy
    # loescht unter Ergebnisse/ nichts.
    say ""
    say "--- Ergebnisse (Ergebnisse/<Projekt>/) ---"

    local results csv
    results="$(find "$REPO_DIR/Ergebnisse" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' 2>/dev/null | sort || true)"

    if [ -z "$results" ]; then
        say "  (noch keine)"
        return 0
    fi

    printf '%s\n' "$results" | while IFS= read -r name; do
        csv="$REPO_DIR/Ergebnisse/$name/Simulation_Results.csv"
        if [ -f "$csv" ]; then
            say "  $name  ($(($(wc -l <"$csv") - 1)) Datensaetze)"
        else
            say "  $name  (noch keine CSV)"
        fi
    done
}

usage() {
    cat <<'TEXT'
Aufruf: bash scripts/sim-runner.sh <befehl> [argument]

  doctor              Prueft Werkzeuge und Pfade, bevor ein Lauf startet   [Projekt]
  build               dotnet build + libpicogk.so neben die DLL legen
  start               Startet den Lauf in einer tmux-Sitzung (baut bei Bedarf) [Projekt]
  stop                Beendet Lauf, tmux-Sitzung und haengende Kindprozesse
  status [zeilen]     Laeuft was? Wie lange? Letzte Logzeilen                [Projekt]
  log [zeilen] [no]   Logausgabe; "no" als zweites Argument = nicht mitlaufen
  results [zeilen]    Kopfzeile und letzte Zeilen der Ergebnis-CSV           [Projekt]
  projects            Welche Projekte kennt dieser Server, wo liegen Ergebnisse
  running             Exit-Code 0, wenn ein Lauf aktiv ist (fuer Skripte)
  hash                Gibt den Stand des zuletzt hochgeladenen Codes aus

Umgebungsvariablen: SIM_SESSION (tmux-Name, Vorgabe "sim"),
                    SIM_PROJECT (Projektname, OHNE Vorgabe)

Die mit [Projekt] markierten Befehle brauchen SIM_PROJECT. Es gibt bewusst keinen
Standard mehr: ein Vertipper wuerde sonst klaglos ein anderes Projekt rechnen und
dessen Ergebnisse ueberschreiben. Welche Namen es gibt, sagt "projects".

    SIM_PROJECT=MantaAuv bash scripts/sim-runner.sh status
TEXT
}

case "${1:-}" in
    doctor)     cmd_doctor ;;
    build)      cmd_build ;;
    projects)   cmd_projects ;;
    start)      cmd_start ;;
    stop)       cmd_stop ;;
    status)     cmd_status "${2:-12}" ;;
    log)        cmd_log "${2:-40}" "${3:-yes}" ;;
    results)    cmd_results "${2:-10}" ;;
    running)    is_running ;;
    hash)       cat "$HASH_FILE" 2>/dev/null || echo "unbekannt" ;;
    _foreground) cmd_foreground ;;
    ""|-h|--help|help) usage ;;
    *)          say "Unbekannter Befehl: $1"; say ""; usage; exit 2 ;;
esac
