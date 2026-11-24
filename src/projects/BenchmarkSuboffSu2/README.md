# BenchmarkSuboffSu2

This benchmark project pairs with `BenchmarkSuboffOpenFoam` as an identical-geometry SU2 twin for the DARPA SUBOFF AFF-1 axisymmetric bare hull (Groves, Huang & Chang 1989) at 1:10 model scale (L = 435.61 mm, D = 50.80 mm) and Re_L = 1.2e7. The watertight analytical hull STL is produced via `DarpaSuboffHullBuilder` and `DirectGeometryKernel` without starting PicoGK.

Gmsh 3D generates a `Y0`-symmetric prism boundary-layer mesh (`TargetYPlus = 1.0`) with a detail refinement box for incompressible SST RANS evaluation in SU2 (`ReferenceLength = 0.43561`, `ReferenceAreaMetric = "WettedArea"`). `BenchmarkSuboffSu2FitnessCalculator` minimizes drag and records the relative error `Suboff_CtRelErr` against the literature total drag coefficient C_T = 0.00314.
