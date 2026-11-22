#!/bin/bash
# Abort script if any critical command fails
set -e

echo "Starting rootless server setup for PicoGK, SU2, Gmsh, and .NET..."

# ---------------------------------------------------------
# 1. DIRECTORY STRUCTURE & MINICONDA
# ---------------------------------------------------------
echo "Creating directory structure..."
# Target project directory on the server (matches RemoteDir in scripts/sim.ps1)
APP_DIR="$HOME/Documents/FluidForge"
mkdir -p ~/software "$APP_DIR"

if [ ! -d "$HOME/miniconda" ]; then
    echo "Installing Miniconda..."
    wget https://repo.anaconda.com/miniconda/Miniconda3-latest-Linux-x86_64.sh -O ~/miniconda.sh
    bash ~/miniconda.sh -b -u -p ~/miniconda
    ~/miniconda/bin/conda init bash
    rm ~/miniconda.sh
fi

# Activate Miniconda for this script
source ~/miniconda/etc/profile.d/conda.sh

# ---------------------------------------------------------
# 2. SYSTEM LIBRARIES VIA CONDA
# ---------------------------------------------------------
echo "Installing system libraries and tools via Conda..."
conda install -y -c conda-forge git boost-cpp tbb-devel blosc pkg-config \
xorg-libxrandr xorg-libxinerama xorg-libxcursor xorg-libxi xorg-xvfb-server tmux \
xorg-libxext xorg-randrproto xorg-renderproto xorg-libxrender xorg-xorgproto \
gcc_linux-64 gxx_linux-64 mesalib libglu freeglut xorg-libx11 openmpi

# ---------------------------------------------------------
# 3. DOWNLOAD EXTERNAL TOOLS (CMake, SU2, Gmsh)
# ---------------------------------------------------------
echo "Downloading external tools..."
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
    # Handle differing top-level directory names inside the archive
    mv SU2-v7.5.1-linux64-mpi SU2-7.5.1 || mv SU2-v7.5.1-linux64 SU2-7.5.1 || mv bin SU2-7.5.1
    rm su2.zip
fi

# Copy SU2 binaries into ~/software/bin
echo "Copying SU2 into ~/software/bin..."
mkdir -p $HOME/software/bin

if [ -d "$HOME/software/SU2-7.5.1/bin" ]; then
    cp -r $HOME/software/SU2-7.5.1/bin/* $HOME/software/bin/
else
    cp -r $HOME/software/SU2-7.5.1/* $HOME/software/bin/
fi

chmod +x $HOME/software/bin/SU2_CFD

# The official SU2 "-mpi" binary is statically linked against MPICH. It requires
# MPICH mpiexec (Hydra), not OpenMPI: under OpenMPI, "-n 12" launches twelve
# independent serial copies that overwrite each other's files without error.
# Create a dedicated conda environment so base OpenMPI remains untouched.
if [ ! -x "$HOME/miniconda/envs/mpich/bin/mpiexec" ]; then
    conda create -y -n mpich -c conda-forge mpich
fi

# Gmsh 4.11.1
if [ ! -d "$HOME/software/gmsh-4.11.1-Linux64" ]; then
    wget https://gmsh.info/bin/Linux/gmsh-4.11.1-Linux64.tgz
    tar -zxvf gmsh-4.11.1-Linux64.tgz
    rm gmsh-4.11.1-Linux64.tgz
fi

# ---------------------------------------------------------
# 3.5. MANUAL OPENGL HEADERS (NO SUDO)
# ---------------------------------------------------------
echo "Downloading OpenGL headers from the Mesa repository..."
mkdir -p $HOME/miniconda/include/GL
mkdir -p $HOME/miniconda/include/KHR

wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/gl.h -O $HOME/miniconda/include/GL/gl.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glext.h -O $HOME/miniconda/include/GL/glext.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glcorearb.h -O $HOME/miniconda/include/GL/glcorearb.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/KHR/khrplatform.h -O $HOME/miniconda/include/KHR/khrplatform.h

# ---------------------------------------------------------
# 4. COMPILE PICOGK (Headless)
# ---------------------------------------------------------
echo "Preparing PicoGK..."
cd ~/software
if [ ! -d "$HOME/software/PicoGK" ]; then
    git clone --recursive https://github.com/leap71/PicoGKRuntime.git ~/software/PicoGK
else
    echo "Updating existing PicoGK repository..."
    cd ~/software/PicoGK
    git pull origin main
    git submodule update --init --recursive
    cd ~/software
fi

# Apply C++ duration formatting fix in PicoGKTrace.h
echo "Applying C++ fix to PicoGKTrace.h..."
sed -i 's/std::cerr << oMS << " elapsed\\n";/std::cerr << oMS.count() << " elapsed\\n";/g' ~/software/PicoGK/Source/PicoGKTrace.h

echo "Compiling PicoGK..."
export PKG_CONFIG_PATH=$HOME/miniconda/lib/pkgconfig:$PKG_CONFIG_PATH
export CFLAGS="-I$HOME/miniconda/include"
export CXXFLAGS="-I$HOME/miniconda/include"
export LDFLAGS="-L$HOME/miniconda/lib"

rm -rf ~/software/PicoGK/build/* || true
mkdir -p ~/software/PicoGK/build
cd ~/software/PicoGK/build

# IMPORTANT: -DGLFW_BUILD_X11=ON is required so xvfb-run can attach a virtual display
~/software/cmake-3.29.3-linux-x86_64/bin/cmake -DCMAKE_PREFIX_PATH=$HOME/miniconda -DBoost_ROOT=$HOME/miniconda -DBoost_NO_SYSTEM_PATHS=ON -DPICOGK_NO_VIEWER=ON -DGLFW_BUILD_WAYLAND=OFF -DGLFW_BUILD_X11=ON -DCMAKE_POLICY_DEFAULT_CMP0144=NEW -DPKG_CONFIG_EXECUTABLE=$HOME/miniconda/bin/pkg-config ..
make -j $(nproc)

# ---------------------------------------------------------
# 5. DOTNET & C# PROJECT SETUP
# ---------------------------------------------------------
echo "Installing .NET 9.0..."
cd ~/software
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x ./dotnet-install.sh
env LD_LIBRARY_PATH="" ./dotnet-install.sh --channel 9.0
export PATH=$HOME/.dotnet:$PATH
rm dotnet-install.sh

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
# 6. HEADLESS GRAPHICS ENVIRONMENT (OPENGL BYPASS)
# ---------------------------------------------------------
echo "Configuring headless graphics environment..."
echo 'export MESA_GL_VERSION_OVERRIDE=4.1' >> ~/.bashrc
echo 'export MESA_GLSL_VERSION_OVERRIDE=410' >> ~/.bashrc
echo 'export LIBGL_ALWAYS_SOFTWARE=1' >> ~/.bashrc
echo 'export DISPLAY=:99' >> ~/.bashrc

source ~/.bashrc

echo "SETUP COMPLETED SUCCESSFULLY!"
echo "IMPORTANT: Please log out of the SSH session and log back in so all environment variables take effect."
