using System.IO;
using System.Text.RegularExpressions;
using Xunit;

using MyPicoGkProject.Core;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-28: die Startskripte sind die einzige Stelle, an der zwischen Projekten
    /// umgeschaltet wird — und die einzige, die dieses Testprojekt nicht ausführen kann
    /// (es gibt kein bash auf dem Entwicklungsrechner, und die Windows-Seite redet per SSH
    /// mit einem Server).
    ///
    /// <para>
    /// Diese Tests prüfen deshalb <b>Text</b>, nicht Verhalten. Das ist wenig, aber es ist
    /// genau das Wenige, das eine ganze Nacht Rechenzeit retten kann: sie schlagen an, wenn
    /// jemand den stillen Projekt-Standard wieder einbaut oder die Umschaltkette auftrennt.
    /// Sie ersetzen <b>keinen</b> Lauf auf dem Server.
    /// </para>
    /// </summary>
    public class ScriptContractTests
    {
        private static string RepoRoot
        {
            get
            {
                string? projectsRoot = ProjectPaths.ResolveProjectsRoot();

                Assert.True(projectsRoot != null, "Das Verzeichnis src/projects/ wurde nicht gefunden.");

                // src/projects -> src -> Repo-Wurzel
                return Path.GetDirectoryName(Path.GetDirectoryName(projectsRoot)!)!;
            }
        }

        /// <summary>Der Ordner <c>scripts/</c> im Repo-Wurzelverzeichnis.</summary>
        private static string ScriptsDirectory
        {
            get
            {
                string scripts = Path.Combine(RepoRoot, "scripts");

                Assert.True(Directory.Exists(scripts), $"Der Ordner scripts/ fehlt (gesucht: {scripts}).");

                return scripts;
            }
        }

        private static string Read(string fileName)
        {
            string file = Path.Combine(ScriptsDirectory, fileName);

            Assert.True(File.Exists(file), $"scripts/{fileName} fehlt.");

            return File.ReadAllText(file);
        }

        private static string ReadRepoFile(string relativePath)
        {
            string file = Path.Combine(RepoRoot, relativePath);

            Assert.True(File.Exists(file), $"{relativePath} fehlt.");

            return File.ReadAllText(file);
        }

        /// <summary>
        /// Dieselbe Datei ohne ihre Kommentarzeilen.
        ///
        /// <para>
        /// Gebraucht für die Suche nach Mustern, die in der <b>Erklärung</b> vorkommen dürfen
        /// und im <b>Code</b> nicht: der Kommentar über <c>PROJECT_NAME</c> zitiert die alte
        /// Zeile <c>${SIM_PROJECT:-MantaAuv}</c>, damit man später nachlesen kann, was daran
        /// falsch war. Genau diese Erklärung ist der Grund, warum der Fehler nicht
        /// zurückkommt — sie darf den Test nicht auslösen.
        /// </para>
        /// </summary>
        private static string WithoutComments(string text)
        {
            var kept = new System.Collections.Generic.List<string>();

            foreach (string line in text.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("#")) kept.Add(line);
            }

            return string.Join("\n", kept);
        }

        /// <summary>
        /// <c>SIM_PROJECT</c> darf keinen Ersatzwert haben. Stand dort einmal
        /// <c>${SIM_PROJECT:-MantaAuv}</c>, rechnete jeder Vertipper klaglos MantaAuv —
        /// stundenlang, und die Ergebnisse landeten in dessen Ordner über der CSV eines
        /// echten Laufs. Die leere Form <c>${SIM_PROJECT:-}</c> ist erlaubt und gewollt:
        /// sie hält <c>set -u</c> zufrieden, und <c>require_project</c> bricht danach ab.
        /// </summary>
        [Fact]
        public void The_Runner_Has_No_Silent_Project_Default()
        {
            string runner = Read("sim-runner.sh");

            var silentDefault = Regex.Match(WithoutComments(runner), @"\$\{SIM_PROJECT:-\s*[^}\s]+\s*\}");

            Assert.False(silentDefault.Success,
                "scripts/sim-runner.sh setzt wieder einen Projekt-Standard: "
                + silentDefault.Value
                + ". Ein Vertipper in SIM_PROJECT rechnet damit stillschweigend das falsche "
                + "Projekt. Richtig ist ${SIM_PROJECT:-} plus require_project.");

            Assert.Contains("require_project", runner);
        }

        /// <summary>
        /// Der Projektname muss ausdrücklich in die tmux-Sitzung hinein. Die Kommandozeile
        /// startet der tmux-<b>Server</b>, und dessen Umgebung stammt von dem Aufruf, der ihn
        /// irgendwann hochgezogen hat — auf Vererbung ist hier kein Verlass.
        /// </summary>
        [Fact]
        public void The_Runner_Passes_The_Project_Into_The_Tmux_Session()
        {
            Assert.Contains("SIM_PROJECT='$PROJECT_NAME'", Read("sim-runner.sh"));
        }

        /// <summary>
        /// Beide Seiten müssen den Befehl <c>projects</c> kennen — er ist die Antwort auf
        /// „wie heißt mein Projekt eigentlich", und genau die braucht man, wenn ohne Namen
        /// nichts mehr läuft.
        /// </summary>
        [Fact]
        public void Both_Scripts_Know_The_Projects_Command()
        {
            Assert.Matches(@"projects\)\s+cmd_projects", Read("sim-runner.sh"));
            Assert.Contains("'projects'", Read("sim.ps1"));
        }

        /// <summary>
        /// Die Umschaltkette von Windows zum Programm: <c>-Project</c> → <c>SIM_PROJECT</c>
        /// → <c>dotnet … "$PROJECT_NAME"</c>. Reißt ein Glied, wird still das falsche
        /// Projekt gerechnet (oder gar keines) — deshalb steht jedes Glied hier.
        /// </summary>
        [Fact]
        public void The_Project_Switch_Reaches_The_Program()
        {
            Assert.Contains("SIM_PROJECT='$Project'", Read("sim.ps1"));

            string runner = Read("sim-runner.sh");

            Assert.Contains("PROJECT_NAME=\"${SIM_PROJECT:-}\"", runner);
            Assert.Contains("dotnet \"$APP_DLL\" \"$PROJECT_NAME\"", runner);
        }

        /// <summary>
        /// <c>doctor</c> fragt die wirksame Konfiguration beim Programm ab und fischt sie
        /// nicht mehr mit <c>sed</c> aus einer einzelnen JSON: seit TODO-22 sind es vier
        /// Schichten, und die Datei allein ist längst nicht mehr die maßgebliche.
        /// </summary>
        [Fact]
        public void Doctor_Asks_The_Program_For_The_Effective_Configuration()
        {
            string runner = Read("sim-runner.sh");

            Assert.Contains("--print-config", runner);
            Assert.Contains("--list-projects", runner);
        }

        /// <summary>
        /// Der Upload-Filter schützt die maschinenspezifischen Dateien — auch die neuen im
        /// Projektordner, weil er ein Namensmuster ist und kein Pfad. Und er hält die
        /// Windows-Seite selbst vom Server fern: der Fingerabdruck wird über genau diese
        /// Liste gebildet, eine Korrektur in einer README würde sonst einen laufenden Lauf
        /// neu starten.
        /// </summary>
        [Fact]
        public void The_Deploy_Filter_Still_Protects_The_Machine_Specific_Files()
        {
            string sim = Read("sim.ps1");

            Assert.Contains("*.local.json", sim);
            Assert.Contains(JsonConfigLoader.MachineSpecificSuffix, sim);
        }

        /// <summary>
        /// TODO-31: Jeder ssh- und scp-Aufruf trägt die Zeitgrenzen. Ein einziger Aufruf ohne
        /// sie genügt, damit eine tote VPN-Verbindung wieder unbegrenzt und stumm hängt — und
        /// zwar genau an der Stelle, die man beim Nachrüsten vergessen hat.
        /// </summary>
        [Fact]
        public void Every_Ssh_And_Scp_Call_Carries_The_Timeouts()
        {
            string code = WithoutComments(Read("sim.ps1"));

            var calls = Regex.Matches(code, @"&\s*(ssh|scp)\b[^\r\n]*");
            Assert.NotEmpty(calls);

            foreach (Match call in calls)
            {
                Assert.True(call.Value.Contains("@SshOptions"),
                    $"Dieser Aufruf in scripts/sim.ps1 hat keine Zeitgrenzen: {call.Value.Trim()}");
            }
        }

        /// <summary>
        /// Die Zeitgrenzen sind bewusst großzügig: unter Volllast antwortet der Server träge, und
        /// zu knappe Werte haben früher gesunde Verbindungen abgebrochen. Wer sie verschärft, soll
        /// an diesem Test vorbei — und dabei die Begründung im Skript lesen.
        /// </summary>
        [Fact]
        public void The_Ssh_Timeouts_Stay_Generous()
        {
            string sim = Read("sim.ps1");

            int Value(string option)
            {
                Match match = Regex.Match(sim, $@"'{option}=(\d+)'");
                Assert.True(match.Success, $"{option} fehlt in $SshOptions (scripts/sim.ps1).");
                return int.Parse(match.Groups[1].Value);
            }

            Assert.True(Value("ConnectTimeout") >= 60, "ConnectTimeout unter 60 s bricht unter Volllast gesunde Verbindungen ab.");
            Assert.True(Value("ServerAliveInterval") * Value("ServerAliveCountMax") >= 180,
                "Unter 3 Minuten Funkstille sollte keine Verbindung als tot gelten.");
        }

        /// <summary>
        /// TODO-31: Wird sim.ps1 fetch oder status ohne explizites -Project aufgerufen, fragt
        /// das Skript per 'lastproject' auf dem Server nach dem zuletzt gerechneten Projekt,
        /// statt stillschweigend die Standard-Vorgabe zu nutzen.
        /// </summary>
        [Fact]
        public void Fetch_And_Status_Query_Lastproject_When_No_Project_Is_Given()
        {
            string sim = Read("sim.ps1");

            Assert.Contains("lastproject", sim);
            Assert.Contains("PSBoundParameters.ContainsKey('Project')", sim);
        }

        /// <summary>
        /// TODO-31: .last_project liegt wie .last_exit nur auf dem Server und gehört
        /// nicht ins Git-Repository.
        /// </summary>
        [Fact]
        public void Last_Project_File_Is_Ignored_In_Gitignore()
        {
            string gitignore = ReadRepoFile(".gitignore");

            Assert.Contains(".last_project", gitignore);
        }

        /// <summary>
        /// TODO-31: Der lokale Zielordner für Serverergebnisse enthält den Projektnamen,
        /// damit Ergebnisse verschiedener Projekte sich nicht im selben Zeitstempel-Ordner
        /// vermischen: Serverergebnisse/<Server>_<Projekt>_<Zeitstempel>/.
        /// </summary>
        [Fact]
        public void The_Fetch_Target_Folder_Contains_The_Project_Name()
        {
            string sim = Read("sim.ps1");

            Assert.Matches(@"\$\{Server\}_\$\{Project\}_\$stamp", sim);
        }

        /// <summary>
        /// TODO-31: Der Befehl 'pack' auf dem Server schnürt das Ergebnis-Archiv und kennt
        /// die drei Umfänge standard (CSV, Log, Config, Verläufe), champion (+Champion-VTUs)
        /// und all (gesamter Ergebnisordner).
        /// </summary>
        [Fact]
        public void The_Runner_Pack_Command_Knows_The_Three_Scopes()
        {
            string runner = Read("sim-runner.sh");

            Assert.Matches(@"pack\)", runner);
            Assert.Contains("standard", runner);
            Assert.Contains("champion", runner);
            Assert.Contains("all", runner);
        }

        /// <summary>
        /// TODO-31: sim-runner.sh merkt sich in cmd_foreground das gerechnete Projekt in
        /// .last_project und gibt es über den Befehl 'lastproject' (ohne require_project) aus.
        /// </summary>
        [Fact]
        public void The_Runner_Has_The_Lastproject_Command()
        {
            string runner = Read("sim-runner.sh");

            Assert.Contains(".last_project", runner);
            Assert.Matches(@"lastproject\)", runner);
        }
    }
}
