# BenchmarkNaca0012

This benchmark project validates FluidForge's 2D external-flow SU2 pipeline against the NASA Langley Turbulence Modeling Resource (TMR) NACA 0012 case at chord Reynolds number Re_c = 6e6, Mach 0.15, and the Spalart-Allmaras (SA) turbulence model. A closed-trailing-edge 1.0 m chord NACA 0012 airfoil contour is meshed at fine verification resolution with Gmsh 2D and evaluated across alpha = 0 deg and alpha = 10 deg operating points.

Geometry generation runs purely analytically via `DirectGeometryKernel` and `Planar2D`, while `BenchmarkNaca0012FitnessCalculator` compares the simulated drag and lift coefficients against grid-converged CFL3D/FUN3D reference data from `Naca2DTmrBenchmark`. Relative and absolute TMR discrepancies (`Tmr_CdRelErr_Alpha0`, `Tmr_ClAbsErr_Alpha0`, `Tmr_CdRelErr_Alpha10`, `Tmr_ClRelErr_Alpha10`) are recorded in `PassiveParameters` alongside the drag minimization objective.
