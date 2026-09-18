#!/bin/bash
# Bricht das Skript ab, falls ein kritischer Befehl fehlschlägt
set -e

echo "🚀 Starte bombensicheres Setup für PicoGK, SU2 und Co. ohne Sudo..."

# ---------------------------------------------------------
# 1. ORDNERSTRUKTUR & MINICONDA
# ---------------------------------------------------------
echo "📁 Erstelle Ordnerstruktur..."
mkdir -p ~/software ~/Documents/Automatisierung_v2

if [ ! -d "$HOME/miniconda" ]; then
    echo "🐍 Installiere Miniconda..."
    wget https://repo.anaconda.com/miniconda/Miniconda3-latest-Linux-x86_64.sh -O ~/miniconda.sh
    bash ~/miniconda.sh -b -u -p ~/miniconda
    ~/miniconda/bin/conda init bash
    rm ~/miniconda.sh
fi

# Miniconda für dieses laufende Skript aktivieren
source ~/miniconda/etc/profile.d/conda.sh

# ---------------------------------------------------------
# 2. SYSTEM-BIBLIOTHEKEN ÜBER CONDA
# ---------------------------------------------------------
echo "📦 Installiere System-Bibliotheken und Tools via Conda..."
conda install -y -c conda-forge git boost-cpp tbb-devel blosc pkg-config \
xorg-libxrandr xorg-libxinerama xorg-libxcursor xorg-libxi xorg-xvfb-server tmux \
xorg-libxext xorg-randrproto xorg-renderproto xorg-libxrender xorg-xorgproto \
gcc_linux-64 gxx_linux-64 mesalib libglu freeglut xorg-libx11 openmpi

# ---------------------------------------------------------
# 3. WERKZEUGE HERUNTERLADEN (CMake, SU2, Gmsh)
# ---------------------------------------------------------
echo "🛠️ Lade externe Werkzeuge herunter..."
cd ~/software

# CMake 3.29.3
if [ ! -d "$HOME/software/cmake-3.29.3-linux-x86_64" ]; then
    wget https://github.com/Kitware/CMake/releases/download/v3.29.3/cmake-3.29.3-linux-x86_64.tar.gz
    tar -zxvf cmake-3.29.3-linux-x86_64.tar.gz
    rm cmake-3.29.3-linux-x86_64.tar.gz
fi

# SU2 CFD 7.5.1
if [ ! -d "$HOME/software/SU2-7.5.1" ]; then
    wget https://github.com/su2code/SU2/releases/download/v7.5.1/SU2-v7.5.1-linux64-mpi.zip -O su2.zip
    unzip su2.zip
    # Fängt die unterschiedlichen Ordnernamen nach dem Entpacken ab
    mv SU2-v7.5.1-linux64-mpi SU2-7.5.1 || mv SU2-v7.5.1-linux64 SU2-7.5.1 || mv bin SU2-7.5.1
    rm su2.zip
fi

# NEU: Ordnerstruktur für C# anpassen
echo "📂 Kopiere SU2 in den bin-Ordner für C#..."
mkdir -p $HOME/software/bin

# Prüfen, ob SU2 in einem Unterordner 'bin' entpackt wurde oder direkt im Hauptordner liegt,
# und die Dateien entsprechend verschieben, damit der Pfad exakt stimmt.
if [ -d "$HOME/software/SU2-7.5.1/bin" ]; then
    cp -r $HOME/software/SU2-7.5.1/bin/* $HOME/software/bin/
else
    cp -r $HOME/software/SU2-7.5.1/* $HOME/software/bin/
fi

# Ausführrechte zur Sicherheit nochmal erzwingen
chmod +x $HOME/software/bin/SU2_CFD

# Gmsh 4.11.1
if [ ! -d "$HOME/software/gmsh-4.11.1-Linux64" ]; then
    wget https://gmsh.info/bin/Linux/gmsh-4.11.1-Linux64.tgz
    tar -zxvf gmsh-4.11.1-Linux64.tgz
    rm gmsh-4.11.1-Linux64.tgz
fi

# ---------------------------------------------------------
# 3.5. MANUELLER OPENGL-HEADER HACK (Sudo umgehen)
# ---------------------------------------------------------
echo "📥 Lade OpenGL-Header über das offizielle Mesa-Repository herunter..."
mkdir -p $HOME/miniconda/include/GL
mkdir -p $HOME/miniconda/include/KHR

wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/gl.h -O $HOME/miniconda/include/GL/gl.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glext.h -O $HOME/miniconda/include/GL/glext.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glcorearb.h -O $HOME/miniconda/include/GL/glcorearb.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/KHR/khrplatform.h -O $HOME/miniconda/include/KHR/khrplatform.h

# ---------------------------------------------------------
# 4. PICOGK KOMPILIEREN (Headless)
# ---------------------------------------------------------
echo "🧬 Bereite PicoGK vor..."
cd ~/software
if [ ! -d "$HOME/software/PicoGK" ]; then
    git clone --recursive https://github.com/leap71/PicoGKRuntime.git ~/software/PicoGK
else
    echo "🔄 Aktualisiere bestehendes PicoGK-Repository..."
    cd ~/software/PicoGK
    git pull origin main
    git submodule update --init --recursive
    cd ~/software
fi

# Automatischer Ersatz für den manuellen Nano-Fix in PicoGKTrace.h
echo "🔧 Wende C++ Fix auf PicoGKTrace.h an..."
sed -i 's/std::cerr << oMS << " elapsed\\n";/std::cerr << oMS.count() << " elapsed\\n";/g' ~/software/PicoGK/Source/PicoGKTrace.h

echo "🔨 Kompiliere PicoGK..."
export PKG_CONFIG_PATH=$HOME/miniconda/lib/pkgconfig:$PKG_CONFIG_PATH
export CFLAGS="-I$HOME/miniconda/include"
export CXXFLAGS="-I$HOME/miniconda/include"
export LDFLAGS="-L$HOME/miniconda/lib"

rm -rf ~/software/PicoGK/build/* || true
mkdir -p ~/software/PicoGK/build
cd ~/software/PicoGK/build

# WICHTIGER FIX HIER: -DGLFW_BUILD_X11=ON (vorher OFF), damit xvfb-run funktioniert!
~/software/cmake-3.29.3-linux-x86_64/bin/cmake -DCMAKE_PREFIX_PATH=$HOME/miniconda -DBoost_ROOT=$HOME/miniconda -DBoost_NO_SYSTEM_PATHS=ON -DPICOGK_NO_VIEWER=ON -DGLFW_BUILD_WAYLAND=OFF -DGLFW_BUILD_X11=ON -DCMAKE_POLICY_DEFAULT_CMP0144=NEW -DPKG_CONFIG_EXECUTABLE=$HOME/miniconda/bin/pkg-config ..
make -j $(nproc)

# ---------------------------------------------------------
# 5. DOTNET & C# PROJEKT SETUP
# ---------------------------------------------------------
echo "🌐 Installiere .NET 9.0..."
cd ~/software
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x ./dotnet-install.sh
env LD_LIBRARY_PATH="" ./dotnet-install.sh --channel 9.0
export PATH=$HOME/.dotnet:$PATH
rm dotnet-install.sh

echo "⚙️ Initialisiere C# Projekt..."
cd ~/Documents/Automatisierung_v2
# Prüfen, ob Projekt schon existiert
if [ ! -f "Automatisierung_v2.csproj" ]; then
    dotnet new console
    dotnet add package PicoGK
fi

# Kopiere kompilierte Bibliothek ins Zielverzeichnis (Dynamische Suche)
mkdir -p ~/Documents/Automatisierung_v2/bin/Debug/net9.0/

echo "🔍 Suche nach kompilierter Bibliothek..."
PICOGK_LIB=$(find ~/software/PicoGK/build -iname "*picogk.so" | head -n 1)

if [ -n "$PICOGK_LIB" ]; then
    # Wir nennen sie beim Kopieren explizit libpicogk.26.2.so, damit das C#-Paket sie erkennt!
    cp "$PICOGK_LIB" ~/Documents/Automatisierung_v2/bin/Debug/net9.0/libpicogk.26.2.so
    
    # Zur Sicherheit legen wir auch die Standard-Version ab
    cp "$PICOGK_LIB" ~/Documents/Automatisierung_v2/bin/Debug/net9.0/libpicogk.so
    
    echo "✅ Bibliothek erfolgreich kopiert von: $PICOGK_LIB"
else
    echo "❌ FEHLER: Bibliothek wurde nicht gefunden. Der Build ist vorher abgebrochen!"
    exit 1
fi

# ---------------------------------------------------------
# 6. SERVER-HACK FÜR HEADLESS PICO-GK (OPENGL BYPASS)
# ---------------------------------------------------------
echo "🔧 Konfiguriere Grafik-Bypass für den Headless-Betrieb..."
echo 'export MESA_GL_VERSION_OVERRIDE=4.1' >> ~/.bashrc
echo 'export MESA_GLSL_VERSION_OVERRIDE=410' >> ~/.bashrc
echo 'export LIBGL_ALWAYS_SOFTWARE=1' >> ~/.bashrc
echo 'export DISPLAY=:99' >> ~/.bashrc

# Kurz neu laden, damit es auch im laufenden Skript greift
source ~/.bashrc

echo "✅ SETUP ERFOLGREICH ABGESCHLOSSEN!"
echo "WICHTIG: Bitte logge dich nun einmal aus dem Server aus und wieder ein, damit alle Umgebungsvariablen aktiv werden!"