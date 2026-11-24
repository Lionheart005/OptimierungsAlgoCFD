# BenchmarkPipeBend90

This benchmark project validates FluidForge's 3D internal-flow OpenFOAM pipeline against classical Idelchik and Miller turbulent 90-degree smooth circular pipe bend pressure-loss correlations at Rc/D = 1.5 and Re_D ~ 1.0e5. The geometry comprises a 30 mm diameter pipe with 5D upstream and 10D downstream straight tangents connected by a circular 90-degree elbow generated via `ShapeKernelPipeBuilder` and `MultiPatchFluidExporter`.

Simulated total pressure drop from `simpleFoam` with k-omega SST and wall functions (`TargetYPlus = 25.0`) is decomposed in `BenchmarkPipeBend90FitnessCalculator` into total loss coefficient `TotalLossCoeff_Zeta`, smooth Blasius straight-pipe friction `StraightFriction_Zeta`, and net secondary bend loss coefficient `BendLossCoeff_Zeta`. All derived loss coefficients are stored in `PassiveParameters` alongside the pressure-drop minimization objective.
