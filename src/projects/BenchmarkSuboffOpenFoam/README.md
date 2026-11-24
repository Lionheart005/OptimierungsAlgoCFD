# BenchmarkSuboffOpenFoam

This benchmark project validates FluidForge's 3D external-flow OpenFOAM pipeline against the classical DARPA SUBOFF AFF-1 axisymmetric bare hull (Groves, Huang & Chang 1989, DTRC/SHD-1298-01) at 1:10 model scale (L = 435.61 mm, D = 50.80 mm) and length Reynolds number Re_L = 1.2e7. A watertight analytical triangle mesh is generated directly via `DarpaSuboffHullBuilder` and `DirectGeometryKernel` without PicoGK voxelization.

The OpenFOAM case uses `Y0` half-model symmetry with a coarse 80 mm background block mesh, 5-6 surface refinement levels, 8 prism wall layers targeting y+ = 80 with `nutkWallFunction`, and wetted-area normalization (`Aref = 0.02994 m^2` for the half hull). `BenchmarkSuboffOpenFoamFitnessCalculator` minimizes drag and records the relative error `Suboff_CtRelErr` against the literature total drag coefficient C_T = 0.00314.
