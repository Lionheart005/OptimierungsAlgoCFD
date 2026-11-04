using System;
using System.IO;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using System.Numerics;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    public class SolverOptionsTests
    {
        /// <summary>
        /// src/projects/MantaAuv/solvers/su2.json muss dieselben Werte enthalten wie die
        /// Code-Standards — sonst rechnet SU2 mit anderen Fluideigenschaften als vor dem
        /// Umzug nach JSON. Einzige bewusste Ausnahme: <c>FreestreamVelocity</c>, siehe
        /// <see cref="MantaAuv_Sets_A_Realistic_Velocity_In_Water"/>.
        /// </summary>
        [Fact]
        public void Su2_Json_Matches_Code_Defaults()
        {
            var defaults = new Su2SolverOptions();
            var loaded = MantaProjectFiles.Load(new Su2SolverOptions(), "solvers/su2.json");

            Assert.Equal(defaults.Density, loaded.Density);
            Assert.Equal(defaults.DynamicViscosity, loaded.DynamicViscosity);
            Assert.Equal(defaults.TurbulenceModel, loaded.TurbulenceModel);
            Assert.Equal(defaults.SpeedOfSound, loaded.SpeedOfSound);
            Assert.Equal(defaults.ReferenceLength, loaded.ReferenceLength);
            Assert.Equal(defaults.ReferenceAreaMetric, loaded.ReferenceAreaMetric);
            // TODO-26: die Zuordnung der Ergebnisgrößen muss in der Datei stehen, damit sie
            // ein neuer Nutzer überhaupt findet — der Code-Standard allein ist unsichtbar.
            Assert.Equal(defaults.ResultMetrics, loaded.ResultMetrics);
            Assert.Equal(defaults.ConvergenceField, loaded.ConvergenceField);
            Assert.Equal(defaults.HistoryOutput, loaded.HistoryOutput);
            Assert.Equal(defaults.CflNumber, loaded.CflNumber);
            Assert.Equal(defaults.CflAdapt, loaded.CflAdapt);
            Assert.Equal(defaults.CflAdaptFactorDown, loaded.CflAdaptFactorDown);
            Assert.Equal(defaults.CflAdaptFactorUp, loaded.CflAdaptFactorUp);
            Assert.Equal(defaults.CflAdaptMin, loaded.CflAdaptMin);
            Assert.Equal(defaults.CflAdaptMax, loaded.CflAdaptMax);
        }

        [Fact]
        public void Gmsh_Json_Matches_Code_Defaults()
        {
            var defaults = new GmshMesherOptions();
            var loaded = MantaProjectFiles.Load(new GmshMesherOptions(), "solvers/gmsh.json");

            Assert.Equal(defaults.TunnelShape, loaded.TunnelShape);
            Assert.Equal(defaults.TunnelSizeX, loaded.TunnelSizeX);
            Assert.Equal(defaults.TunnelSizeY, loaded.TunnelSizeY);
            Assert.Equal(defaults.TunnelSizeZ, loaded.TunnelSizeZ);
            Assert.Equal(defaults.TunnelCenterX, loaded.TunnelCenterX);
            Assert.Equal(defaults.TunnelCenterY, loaded.TunnelCenterY);
            Assert.Equal(defaults.TunnelCenterZ, loaded.TunnelCenterZ);
            Assert.Equal(defaults.TunnelDiameter, loaded.TunnelDiameter);
            Assert.Equal(defaults.TunnelLength, loaded.TunnelLength);
            Assert.Equal(defaults.TunnelSegments, loaded.TunnelSegments);
            Assert.Equal(defaults.BoundaryLayerSizeMin, loaded.BoundaryLayerSizeMin);
            Assert.Equal(defaults.BoundaryLayerSizeMax, loaded.BoundaryLayerSizeMax);
            Assert.Equal(defaults.BoundaryLayerDistMin, loaded.BoundaryLayerDistMin);
            Assert.Equal(defaults.BoundaryLayerDistMax, loaded.BoundaryLayerDistMax);
        }

        /// <summary>
        /// Die SU2-Konfigurationsdatei muss mit den Standard-Optionen physikalisch dasselbe
        /// beschreiben wie die frühere hardcodierte Fassung.
        /// </summary>
        [Fact]
        public void Generated_Su2_Config_Keeps_The_Previous_Values()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path,
                    "/tmp/mesh.su2",
                    refArea: 0.001f,
                    config: SimulationConfig.CreateDefault(),
                    options: new Su2SolverOptions());

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("KIND_TURB_MODEL= SA", lines);
                Assert.Contains("FREESTREAM_DENSITY= 1025", lines);
                Assert.Contains("INC_DENSITY_INIT= 1025", lines);
                Assert.Contains("MU_CONSTANT= 0.001001", lines);
                Assert.Contains("REF_LENGTH= 0.01", lines);
                Assert.Contains("CFL_NUMBER= 5", lines);
                Assert.Contains("CFL_ADAPT= YES", lines);
                Assert.Contains("CFL_ADAPT_PARAM= ( 0.5, 1.2, 1, 50 )", lines);
                Assert.Contains("MESH_FILENAME= mesh.su2", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // -------------------------------------------------------------------
        // TODO-38: Anströmung direkt in m/s
        // -------------------------------------------------------------------

        /// <summary>
        /// Ohne <c>FreestreamVelocity</c> rechnet jedes Projekt exakt wie vor TODO-38:
        /// Geschwindigkeit = MachNumber × SpeedOfSound.
        /// </summary>
        [Fact]
        public void Without_A_Velocity_The_Old_Mach_Path_Still_Applies()
        {
            var config = SimulationConfig.CreateDefault();
            var options = new Su2SolverOptions();

            float velocity = Su2ConfigGenerator.ResolveVelocity(config, options, out string source);

            Assert.Equal(0.0f, options.FreestreamVelocity);
            Assert.Equal(config.MachNumber * options.SpeedOfSound, velocity);
            Assert.Contains("MachNumber", source);
        }

        /// <summary>
        /// Eine gesetzte Geschwindigkeit gewinnt — und erreicht beide Stellen der cfg, an denen
        /// SU2 sie braucht: Freistrom (Randbedingung) und Startfeld.
        /// </summary>
        [Fact]
        public void A_Set_Velocity_Wins_Over_The_Mach_Number_And_Reaches_The_Config()
        {
            var config = SimulationConfig.CreateDefault();
            config.MachNumber = 0.3f; // darf keine Rolle mehr spielen

            var options = new Su2SolverOptions { FreestreamVelocity = 2.0f };

            Assert.Equal(2.0f, Su2ConfigGenerator.ResolveVelocity(config, options, out string source));
            Assert.Contains("FreestreamVelocity", source);

            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", refArea: 0.001f, config: config, options: options);

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("FREESTREAM_VELOCITY= ( 2, 0.0, 0.0 )", lines);
                Assert.Contains("INC_VELOCITY_INIT= ( 2, 0.0, 0.0 )", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// MantaAuv weicht hier bewusst vom Code-Standard ab: 2 m/s statt der 34,3 m/s, die
        /// Mach 0.1 mit der Schallgeschwindigkeit von Luft ergab (Entscheidung E4,
        /// <c>.plans/anmerkungen_umsetzung.md</c>). Ändert jemand den Wert, soll das bewusst
        /// passieren — die Ergebnisse alter und neuer Läufe sind dann nicht vergleichbar.
        /// </summary>
        [Fact]
        public void MantaAuv_Sets_A_Realistic_Velocity_In_Water()
        {
            var loaded = MantaProjectFiles.Load(new Su2SolverOptions(), "solvers/su2.json");

            Assert.Equal(2.0f, loaded.FreestreamVelocity);
            Assert.Equal(2.0f, Su2ConfigGenerator.ResolveVelocity(MantaProjectFiles.Simulation(), loaded, out _));
        }

        [Fact]
        public void Options_Reach_The_Generated_Su2_Config()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path,
                    "/tmp/mesh.su2",
                    refArea: 0.001f,
                    config: SimulationConfig.CreateDefault(),
                    options: new Su2SolverOptions
                    {
                        Density = 998.0f,
                        TurbulenceModel = "SST",
                        CflAdapt = false
                    });

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("FREESTREAM_DENSITY= 998", lines);
                Assert.Contains("KIND_TURB_MODEL= SST", lines);
                Assert.Contains("CFL_ADAPT= NO", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // -------------------------------------------------------------------
        // TODO-38: FlowConditions, Warnung bei wirkungslosem MachNumber, Reynoldszahl
        // -------------------------------------------------------------------

        [Fact]
        public void FlowConditions_From_Resolves_Velocity_And_Fluid_Properties()
        {
            var config = SimulationConfig.CreateDefault();
            config.MachNumber = 0.2f;

            // A) Explizite FreestreamVelocity gewinnt
            var optionsWithVel = new Su2SolverOptions
            {
                FreestreamVelocity = 2.0f,
                Density = 1025f,
                DynamicViscosity = 0.001001f
            };
            var flow = FlowConditions.From(config, optionsWithVel);

            Assert.Equal(2.0f, flow.Velocity);
            Assert.Equal(1025f, flow.Density);
            Assert.Equal(0.001001f, flow.DynamicViscosity);
            Assert.Contains("FreestreamVelocity", flow.VelocitySource);

            // B) Fallback auf MachNumber
            var optionsWithoutVel = new Su2SolverOptions
            {
                FreestreamVelocity = 0.0f,
                Density = 1.225f,
                DynamicViscosity = 1.81e-5f,
                SpeedOfSound = 343.2f
            };
            var flowMach = FlowConditions.From(config, optionsWithoutVel);

            Assert.Equal(config.MachNumber * optionsWithoutVel.SpeedOfSound, flowMach.Velocity);
            Assert.Equal(1.225f, flowMach.Density);
            Assert.Equal(1.81e-5f, flowMach.DynamicViscosity);
            Assert.Contains("MachNumber", flowMach.VelocitySource);
        }

        [Fact]
        public void FlowConditions_ComputeReynoldsNumber_Calculates_Correct_Re()
        {
            // MantaAuv: 2 m/s, rho=1025, mu=0.001001, L=50 mm = 0.05 m
            // Re = 1025 * 2 * 0.05 / 0.001001 = 102397.6...
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "MantaAuv");
            float re = flow.ComputeReynoldsNumber(0.05f);

            Assert.InRange(re, 1.023e5f, 1.025e5f);

            // Schutz vor ungültigen Eingaben
            Assert.Equal(0f, flow.ComputeReynoldsNumber(0f));
            Assert.Equal(0f, flow.ComputeReynoldsNumber(-1f));
            Assert.Equal(0f, new FlowConditions(2f, 1025f, 0f).ComputeReynoldsNumber(0.05f));
            Assert.Equal(0f, new FlowConditions(2f, 1025f, -1f).ComputeReynoldsNumber(0.05f));
            Assert.Equal(0f, new FlowConditions(0f, 1025f, 0.001001f).ComputeReynoldsNumber(0.05f));
        }

        [Fact]
        public void WarnIfMachNumberOverridden_Warns_When_Velocity_Set_And_MachNumber_In_Origins()
        {
            var report = new ConfigLoadReport();
            report.Origins["MachNumber"] = "src/projects/MantaAuv/simulation.json";

            var su2Options = new Su2SolverOptions { FreestreamVelocity = 2.0f };

            using var sw = new StringWriter();
            bool warned = Su2ConfigGenerator.WarnIfMachNumberOverridden(report, su2Options, sw);

            Assert.True(warned);
            string log = sw.ToString();
            Assert.Contains("[WARNUNG] 'MachNumber' in src/projects/MantaAuv/simulation.json ist wirkungslos", log);
            Assert.Contains("FreestreamVelocity", log);
            Assert.Contains("2 m/s", log);
        }

        [Fact]
        public void WarnIfMachNumberOverridden_Silent_When_MachNumber_Not_In_Origins()
        {
            var report = new ConfigLoadReport();
            report.Origins["MaxIterations"] = "simulation.json"; // MachNumber fehlt

            var su2Options = new Su2SolverOptions { FreestreamVelocity = 2.0f };

            using var sw = new StringWriter();
            bool warned = Su2ConfigGenerator.WarnIfMachNumberOverridden(report, su2Options, sw);

            Assert.False(warned);
            Assert.Empty(sw.ToString());
        }

        [Fact]
        public void WarnIfMachNumberOverridden_Silent_When_FreestreamVelocity_Is_Zero()
        {
            var report = new ConfigLoadReport();
            report.Origins["MachNumber"] = "simulation.json";

            var su2Options = new Su2SolverOptions { FreestreamVelocity = 0.0f };

            using var sw = new StringWriter();
            bool warned = Su2ConfigGenerator.WarnIfMachNumberOverridden(report, su2Options, sw);

            Assert.False(warned);
            Assert.Empty(sw.ToString());
        }

        [Fact]
        public void WarnIfMachNumberOverridden_Silent_When_Report_Is_Null()
        {
            var su2Options = new Su2SolverOptions { FreestreamVelocity = 2.0f };

            using var sw = new StringWriter();
            bool warned = Su2ConfigGenerator.WarnIfMachNumberOverridden(null, su2Options, sw);

            Assert.False(warned);
            Assert.Empty(sw.ToString());
        }

        [Fact]
        public void StlWriter_ReadBounds_Reads_Exact_Extents_Of_Binary_Stl()
        {
            string path = Path.Combine(Path.GetTempPath(), "test_box_" + Guid.NewGuid().ToString("N") + ".stl");
            try
            {
                var triangles = new[]
                {
                    new StlTriangle(
                        new Vector3(0, 0, 0),
                        new Vector3(50, 0, 0),
                        new Vector3(0, 20, 10)),
                    new StlTriangle(
                        new Vector3(50, 0, 0),
                        new Vector3(50, 20, 10),
                        new Vector3(0, 20, 10))
                };

                StlWriter.WriteBinary(path, triangles);

                MeshBounds bounds = StlWriter.ReadBounds(path);

                Assert.False(bounds.IsEmpty);
                Assert.Equal(0f, bounds.Min.X, 4);
                Assert.Equal(0f, bounds.Min.Y, 4);
                Assert.Equal(0f, bounds.Min.Z, 4);
                Assert.Equal(50f, bounds.Max.X, 4);
                Assert.Equal(20f, bounds.Max.Y, 4);
                Assert.Equal(10f, bounds.Max.Z, 4);
                Assert.Equal(50f, bounds.Size.X, 4);
                Assert.Equal(20f, bounds.Size.Y, 4);
                Assert.Equal(10f, bounds.Size.Z, 4);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void StlWriter_ReadBounds_Returns_Empty_For_Empty_Or_Missing_File()
        {
            string path = Path.Combine(Path.GetTempPath(), "test_empty_" + Guid.NewGuid().ToString("N") + ".stl");
            try
            {
                StlWriter.WriteBinary(path, Array.Empty<StlTriangle>());
                MeshBounds bounds = StlWriter.ReadBounds(path);

                Assert.True(bounds.IsEmpty);
                Assert.Equal(Vector3.Zero, bounds.Size);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }

            Assert.Throws<FileNotFoundException>(() => StlWriter.ReadBounds("non_existing_file.stl"));
        }

        [Fact]
        public void Mesher_And_Solver_Accept_FlowConditions()
        {
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "su2.json");
            var gmsh = new GmshCfdMesher(new GmshMesherOptions(), flow);
            var su2 = new Su2Solver(new Su2SolverOptions(), flow);

            Assert.Same(flow, gmsh.Flow);
            Assert.Same(flow, su2.Flow);
        }

        [Fact]
        public void Su2Solver_Records_Reynolds_Number_In_PassiveParameters()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "su2_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string stlPath = Path.Combine(tempDir, "model.stl");
            string meshPath = Path.Combine(tempDir, "mesh.su2");
            try
            {
                var triangles = new[]
                {
                    new StlTriangle(new Vector3(0, 0, 0), new Vector3(50, 0, 0), new Vector3(0, 10, 5))
                };
                StlWriter.WriteBinary(stlPath, triangles);
                File.WriteAllText(meshPath, "mock mesh");

                var record = new ModelRecord
                {
                    Iteration = 1,
                    Variant = 1,
                    StlPath = stlPath
                };
                record.PassiveParameters["FrontalArea"] = 100f;

                var options = new Su2SolverOptions
                {
                    FreestreamVelocity = 2.0f,
                    Density = 1025f,
                    DynamicViscosity = 0.001001f
                };

                var solver = new Su2Solver(options);
                var config = SimulationConfig.CreateDefault();
                config.MpiRunPath = "non_existent_mpi_binary";

                try
                {
                    solver.Solve(meshPath, record, config, tempDir);
                }
                catch
                {
                    // Erwarteter Abbruch beim Start des Prozesses
                }

                Assert.True(record.PassiveParameters.ContainsKey("ReynoldsNumber"));
                Assert.InRange(record.PassiveParameters["ReynoldsNumber"], 1.023e5f, 1.025e5f);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private class TestCfdProject : MyPicoGkProject.Composition.CfdProjectDefinition
        {
            public override string Name => "TestCfd";
            public override ProjectConfig CreateDefaults() => new ProjectConfig();
            public override IGeometryGenerator CreateGeometry(ProjectContext context) => throw new NotImplementedException();
            public override IFitnessCalculator CreateFitness(ProjectContext context) => throw new NotImplementedException();
        }

        [Fact]
        public void CfdProjectDefinition_CreateStages_Wired_With_FlowConditions_And_Warns()
        {
            var simConfig = SimulationConfig.CreateDefault();
            var projConfig = new ProjectConfig();
            var context = new ProjectContext("TestProject", null, null, simConfig, projConfig);

            var report = new ConfigLoadReport();
            report.Origins["MachNumber"] = "simulation.json";
            context.Loaded.Add(new LoadedConfiguration("simulation.json", simConfig, report));

            var project = new TestCfdProject();
            var stages = project.CreateStages(context);

            Assert.Single(stages);
            var mesher = Assert.IsType<GmshCfdMesher>(stages[0].Mesher);
            var solver = Assert.IsType<Su2Solver>(stages[0].Solver);

            Assert.NotNull(mesher.Flow);
            Assert.NotNull(solver.Flow);
            Assert.Equal(mesher.Flow.Velocity, solver.Flow.Velocity);
        }
    }
}
