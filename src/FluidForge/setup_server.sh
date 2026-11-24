#!/bin/bash
# Abort script if any critical command fails
set -e

echo "Starting rootless server setup for PicoGK, SU2, Gmsh, and .NET..."

# ---------------------------------------------------------
# 1. DIRECTORY STRUCTURE & MINIFORGE3 (IN $HOME/miniconda)
# ---------------------------------------------------------
echo "Creating directory structure..."
# Target project directory on the server (matches RemoteDir in scripts/sim.ps1)
APP_DIR="$HOME/Documents/FluidForge"
mkdir -p "$HOME/software" "$HOME/software/bin" "$APP_DIR"

if [ ! -d "$HOME/miniconda" ]; then
    echo "Installing Miniforge3 into $HOME/miniconda..."
    wget https://github.com/conda-forge/miniforge/releases/latest/download/Miniforge3-Linux-x86_64.sh -O ~/miniforge.sh
    bash ~/miniforge.sh -b -u -p "$HOME/miniconda"
    "$HOME/miniconda/bin/conda" init bash
    rm -f ~/miniforge.sh
fi

# Activate Conda for this script
source "$HOME/miniconda/etc/profile.d/conda.sh"
export PATH="$HOME/miniconda/bin:$HOME/software/bin:$PATH"

# ---------------------------------------------------------
# 2. SYSTEM LIBRARIES VIA CONDA (conda-forge ONLY)
# ---------------------------------------------------------
echo "Installing system libraries and tools via Conda (conda-forge)..."
EXTRA_CONDA_PKGS=()
if ! command -v unzip >/dev/null 2>&1; then
    EXTRA_CONDA_PKGS+=(unzip)
fi

conda install -y --override-channels -c conda-forge git boost-cpp tbb-devel blosc pkg-config \
xorg-libxrandr xorg-libxinerama xorg-libxcursor xorg-libxi xorg-xvfb-server tmux \
xorg-libxext xorg-randrproto xorg-renderproto xorg-libxrender xorg-xorgproto \
gcc_linux-64 gxx_linux-64 mesalib libglu freeglut xorg-libx11 openmpi "${EXTRA_CONDA_PKGS[@]}"

# Provide xvfb-run fallback wrapper when the Ubuntu xvfb package is not installed
if ! command -v xvfb-run >/dev/null 2>&1; then
    echo "xvfb-run not found; creating fallback wrapper in $HOME/software/bin/xvfb-run..."
    mkdir -p "$HOME/software/bin"
    cat <<'EOF' > "$HOME/software/bin/xvfb-run"
#!/usr/bin/env bash
set -euo pipefail

disp=99
while [ "$#" -gt 0 ]; do
    case "$1" in
        -a|--auto-servernum)
            while [ -e "/tmp/.X${disp}-lock" ] || [ -e "/tmp/.X11-unix/X${disp}" ]; do
                disp=$((disp + 1))
            done
            shift
            ;;
        -n|--server-num)
            disp="${2#:}"
            shift 2
            ;;
        -s|--server-args)
            shift 2
            ;;
        --server-args=*)
            shift
            ;;
        --)
            shift
            break
            ;;
        -*)
            shift
            ;;
        *)
            break
            ;;
    esac
done

if [ "$#" -eq 0 ]; then
    echo "xvfb-run: missing command" >&2
    exit 2
fi

XVFB_BIN="$(command -v Xvfb 2>/dev/null || echo "$HOME/miniconda/bin/Xvfb")"
"$XVFB_BIN" ":${disp}" -screen 0 1280x1024x24 >/dev/null 2>&1 &
XVFB_PID=$!

cleanup() {
    kill "$XVFB_PID" >/dev/null 2>&1 || true
    wait "$XVFB_PID" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

for _ in $(seq 1 20); do
    if ! kill -0 "$XVFB_PID" >/dev/null 2>&1; then
        break
    fi
    if [ -S "/tmp/.X11-unix/X${disp}" ]; then
        break
    fi
    sleep 0.1
done

DISPLAY=":${disp}" "$@"
EOF
    chmod +x "$HOME/software/bin/xvfb-run"
fi

# ---------------------------------------------------------
# 3. DOWNLOAD EXTERNAL TOOLS (CMake, SU2, Gmsh)
# ---------------------------------------------------------
echo "Downloading external tools..."
cd ~/software

# CMake 3.29.3
if [ ! -d "$HOME/software/cmake-3.29.3-linux-x86_64" ]; then
    wget https://github.com/Kitware/CMake/releases/download/v3.29.3/cmake-3.29.3-linux-x86_64.tar.gz
    tar -zxf cmake-3.29.3-linux-x86_64.tar.gz
    rm -f cmake-3.29.3-linux-x86_64.tar.gz
fi

# Ensure unzip is available before SU2 extraction
if ! command -v unzip >/dev/null 2>&1; then
    conda install -y --override-channels -c conda-forge unzip
fi

# SU2 CFD 7.5.1
if [ ! -d "$HOME/software/SU2-7.5.1" ]; then
    wget https://github.com/su2code/SU2/releases/download/v7.5.1/SU2-v7.5.1-linux64-mpi.zip -O su2.zip
    unzip -o su2.zip
    # Handle differing top-level directory names inside the archive
    mv SU2-v7.5.1-linux64-mpi SU2-7.5.1 || mv SU2-v7.5.1-linux64 SU2-7.5.1 || mv bin SU2-7.5.1
    rm -f su2.zip
fi

# Copy SU2 binaries into ~/software/bin
echo "Copying SU2 into ~/software/bin..."
mkdir -p "$HOME/software/bin"

if [ -d "$HOME/software/SU2-7.5.1/bin" ]; then
    cp -r "$HOME/software/SU2-7.5.1/bin/"* "$HOME/software/bin/"
else
    cp -r "$HOME/software/SU2-7.5.1/"* "$HOME/software/bin/"
fi

chmod +x "$HOME/software/bin/SU2_CFD"

# The official SU2 "-mpi" binary is statically linked against MPICH. It requires
# MPICH mpiexec (Hydra), not OpenMPI: under OpenMPI, "-n 12" launches twelve
# independent serial copies that overwrite each other's files without error.
# Create a dedicated conda environment so base OpenMPI remains untouched.
if [ ! -x "$HOME/miniconda/envs/mpich/bin/mpiexec" ]; then
    conda create -y -n mpich --override-channels -c conda-forge mpich
fi

# Gmsh 4.11.1
if [ ! -d "$HOME/software/gmsh-4.11.1-Linux64" ]; then
    wget https://gmsh.info/bin/Linux/gmsh-4.11.1-Linux64.tgz
    tar -zxf gmsh-4.11.1-Linux64.tgz
    rm -f gmsh-4.11.1-Linux64.tgz
fi

# ---------------------------------------------------------
# 3.5. MANUAL OPENGL HEADERS (NO SUDO)
# ---------------------------------------------------------
echo "Downloading OpenGL headers from the Mesa repository..."
mkdir -p "$HOME/miniconda/include/GL"
mkdir -p "$HOME/miniconda/include/KHR"

wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/gl.h -O "$HOME/miniconda/include/GL/gl.h"
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glext.h -O "$HOME/miniconda/include/GL/glext.h"
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glcorearb.h -O "$HOME/miniconda/include/GL/glcorearb.h"
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/KHR/khrplatform.h -O "$HOME/miniconda/include/KHR/khrplatform.h"

# ---------------------------------------------------------
# 3.6. OPENFOAM (ESI v2506 SYSTEM OR ROOTLESS CONDA FALLBACK)
# ---------------------------------------------------------
echo "Checking OpenFOAM (ESI v2506) installation..."
OPENFOAM_BASHRC=""
if [ -f "/usr/lib/openfoam/openfoam2506/etc/bashrc" ]; then
    OPENFOAM_BASHRC="/usr/lib/openfoam/openfoam2506/etc/bashrc"
else
    for candidate in /usr/lib/openfoam/openfoam2*/etc/bashrc; do
        if [ -f "$candidate" ]; then
            OPENFOAM_BASHRC="$candidate"
            break
        fi
    done
fi

if [ -n "$OPENFOAM_BASHRC" ]; then
    echo "System OpenFOAM installation detected: $OPENFOAM_BASHRC (no Conda OpenFOAM install needed)."
elif [ -f "$HOME/miniconda/envs/openfoam/etc/bashrc" ]; then
    echo "Rootless Conda OpenFOAM environment already exists: $HOME/miniconda/envs/openfoam"
else
    echo "No system OpenFOAM found; creating isolated rootless Conda environment 'openfoam' (separate from mpich)..."
    conda create -y -n openfoam -c conda-forge openfoam --override-channels
fi

# ---------------------------------------------------------
# 3.7. OPTIONAL PYTHON OPTIMIZER ENV (opt-py)
# ---------------------------------------------------------
# Keep packages aligned with scripts/optimizers/environment_opt.yml
if [ ! -x "$HOME/miniconda/envs/opt-py/bin/python" ]; then
    echo "Creating optional Python optimizer environment (opt-py)..."
    conda create -y -n opt-py --override-channels -c conda-forge \
        python=3.11 pip "numpy>=1.26" "scipy>=1.12" "pandas>=2.1" "matplotlib>=3.8"
    "$HOME/miniconda/envs/opt-py/bin/pip" install --no-cache-dir "torch>=2.2" --index-url https://download.pytorch.org/whl/cpu
    "$HOME/miniconda/envs/opt-py/bin/pip" install --no-cache-dir "gpytorch>=1.12" "botorch>=0.12" "pymoo>=0.6"
else
    echo "Optional Python optimizer environment (opt-py) already exists."
fi

# ---------------------------------------------------------
# 4. COMPILE PICOGK (Headless)
# ---------------------------------------------------------
echo "Preparing PicoGK..."
PICOGK_COMMIT="1713d29"
cd ~/software
if [ ! -d "$HOME/software/PicoGK" ]; then
    git clone https://github.com/leap71/PicoGKRuntime.git ~/software/PicoGK
    cd ~/software/PicoGK
    git checkout "$PICOGK_COMMIT"
    git submodule update --init --recursive
else
    echo "Updating existing PicoGK repository..."
    cd ~/software/PicoGK
    git fetch --all
    git checkout "$PICOGK_COMMIT"
    git submodule update --init --recursive
fi
cd ~/software

# Apply C++ duration formatting fix in PicoGKTrace.h
echo "Applying C++ fix to PicoGKTrace.h..."
sed -i 's/std::cerr << oMS << " elapsed\\n";/std::cerr << oMS.count() << " elapsed\\n";/g' ~/software/PicoGK/Source/PicoGKTrace.h

echo "Compiling PicoGK..."
export PKG_CONFIG_PATH="$HOME/miniconda/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
export CFLAGS="-I$HOME/miniconda/include"
export CXXFLAGS="-I$HOME/miniconda/include"
export LDFLAGS="-L$HOME/miniconda/lib"

rm -rf ~/software/PicoGK/build/* || true
mkdir -p ~/software/PicoGK/build
cd ~/software/PicoGK/build

# IMPORTANT: -DGLFW_BUILD_X11=ON is required so xvfb-run can attach a virtual display
~/software/cmake-3.29.3-linux-x86_64/bin/cmake \
    -DCMAKE_PREFIX_PATH="$HOME/miniconda" \
    -DBoost_ROOT="$HOME/miniconda" \
    -DBoost_NO_SYSTEM_PATHS=ON \
    -DPICOGK_NO_VIEWER=ON \
    -DGLFW_BUILD_WAYLAND=OFF \
    -DGLFW_BUILD_X11=ON \
    -DCMAKE_POLICY_DEFAULT_CMP0144=NEW \
    -DPKG_CONFIG_EXECUTABLE="$HOME/miniconda/bin/pkg-config" ..
make -j "$(nproc)"

# ---------------------------------------------------------
# 5. DOTNET & C# PROJECT SETUP
# ---------------------------------------------------------
echo "Installing .NET 9.0..."
cd ~/software
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x ./dotnet-install.sh
env LD_LIBRARY_PATH="" ./dotnet-install.sh --channel 9.0
export PATH="$HOME/.dotnet:$PATH"
rm -f dotnet-install.sh

# The C# project is deployed via `sim.ps1 deploy`; here we only place the native
# PicoGK shared library into the expected output directory.
BIN_DIR="$APP_DIR/src/FluidForge/bin/Debug/net9.0"
mkdir -p "$BIN_DIR"

echo "Searching for compiled PicoGK library..."
PICOGK_LIB=$(find ~/software/PicoGK/build -iname "*picogk.so" | head -n 1)

if [ -n "$PICOGK_LIB" ]; then
    # Copy under both the versioned and unversioned names expected by the NuGet package
    cp "$PICOGK_LIB" "$BIN_DIR/libpicogk.26.2.so"
    cp "$PICOGK_LIB" "$BIN_DIR/libpicogk.so"

    echo "Library copied from: $PICOGK_LIB"
else
    echo "ERROR: Compiled PicoGK library not found. Build failed earlier."
    exit 1
fi

# ---------------------------------------------------------
# 6. HEADLESS GRAPHICS ENVIRONMENT & PATH (IDEMPOTENT)
# ---------------------------------------------------------
echo "Configuring headless graphics environment in ~/.bashrc..."
touch ~/.bashrc

add_bashrc_line() {
    local line="$1"
    grep -qF "$line" ~/.bashrc || printf '%s\n' "$line" >> ~/.bashrc
}

add_bashrc_line 'export MESA_GL_VERSION_OVERRIDE=4.1'
add_bashrc_line 'export MESA_GLSL_VERSION_OVERRIDE=410'
add_bashrc_line 'export LIBGL_ALWAYS_SOFTWARE=1'
add_bashrc_line 'export DISPLAY=:99'
add_bashrc_line 'export PATH="$HOME/.dotnet:$HOME/software/bin:$PATH"'

echo "SETUP COMPLETED SUCCESSFULLY!"
echo "IMPORTANT: Please log out of the SSH session and log back in (or run 'sim.ps1 doctor -Server <Alias>') so all interactive shell variables take effect."
