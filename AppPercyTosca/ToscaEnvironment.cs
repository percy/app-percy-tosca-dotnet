using System.Reflection;
using AppPercyTosca.Core;
using Tricentis.Automation.AutomationInstructions.TestActions;
using Tricentis.Automation.Engines.SpecialExecutionTasks;

namespace AppPercyTosca
{
    /// <summary>
    /// <see cref="IToscaEnvironment"/> over the real Tricentis APIs. One instance per executed step,
    /// because the screenshot route needs that step's test action.
    ///
    /// Two of the three routes are reached by reflection rather than a direct call, and the reason is
    /// specific rather than general timidity: <c>MainConfiguration</c> (test configuration
    /// parameters) and <c>Buffers</c> (Tosca buffers) are documented by behaviour but their
    /// namespaces and exact member shapes are not published, and they differ across Tosca releases.
    /// Binding to a guess at compile time turns a wrong guess into "does not build on the customer's
    /// machine"; binding late turns it into a logged warning and a fall back to the Percy module's
    /// own parameters. <c>SpecialExecutionTaskFactory</c> is called directly, because Tricentis
    /// publishes its signature in their own PrintScreen source.
    /// </summary>
    internal class ToscaEnvironment : IToscaEnvironment
    {
        /// <summary>
        /// The mobile engine's own screenshot task, and the engine id to ask for it under. Tosca's
        /// built-in PrintScreen delegates to exactly this pair for mobile, which is why it is the
        /// route used here rather than anything driver-level.
        /// </summary>
        private const string MobileScreenshotTask = "Mobile30PrintScreen";
        private const string MobileEngineId = "ME3.0";

        // Members are probed under several names because these types' shapes are not published.
        private static readonly string[] ConfigurationTypeNames =
            { "MainConfiguration", "Configuration", "TestConfiguration" };
        private static readonly string[] ParameterMapNames =
            { "LoadedTestConfigurationParameter", "LoadedTestConfigurationParameters",
              "TestConfigurationParameters" };
        private static readonly string[] BuffersTypeNames = { "Buffers", "BufferManager", "Buffer" };
        private static readonly string[] GetBufferNames = { "GetBuffer", "GetBufferValue", "Get" };
        private static readonly string[] ValueNames = { "Value", "ValueAsString", "UnresolvedValue" };

        private readonly ISpecialExecutionTaskTestAction _testAction;
        private readonly string _screenshotTaskName;
        private readonly string _screenshotEngineId;

        /// <summary>
        /// Memoized per step: the parameter map costs a reflective walk, and one snapshot reads
        /// several values out of it.
        /// </summary>
        private IReadOnlyDictionary<string, string?>? _parameters;

        internal ToscaEnvironment(
            ISpecialExecutionTaskTestAction testAction,
            string? screenshotTaskName = null,
            string? screenshotEngineId = null)
        {
            _testAction = testAction;
            _screenshotTaskName = screenshotTaskName ?? MobileScreenshotTask;
            _screenshotEngineId = screenshotEngineId ?? MobileEngineId;
        }

        /// <summary>
        /// False: <c>Execute Driver Script</c>, the only route for a raw Appium command, is
        /// documented as working against Tricentis' own device cloud only — so it cannot be relied
        /// on against an App Automate hub, which is where it would matter. Percy on Automate is the
        /// supported way to get App Automate's full capability set from Tosca.
        /// </summary>
        public bool CanExecuteScript => false;

        public string? ExecuteScript(string script) => null;

        public string? TestConfigurationParameter(string name)
        {
            IReadOnlyDictionary<string, string?> parameters = TestConfigurationParameters();
            if (parameters.TryGetValue(name, out string? exact)) return exact;

            foreach (KeyValuePair<string, string?> parameter in parameters)
            {
                if (string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return parameter.Value;
                }
            }
            return null;
        }

        public IReadOnlyDictionary<string, string?> TestConfigurationParameters() =>
            _parameters ??= ReadParameters();

        public string? Buffer(string name)
        {
            object? buffers = SingletonInstance(BuffersTypeNames,
                candidate => Reflect.MemberNames(candidate)
                    .Any(member => GetBufferNames.Contains(member.TrimEnd('(', ')'))));
            if (buffers == null)
            {
                Utils.Log("Could not reach Tosca's buffers, so the Appium session id is not " +
                    "available. Percy on Automate needs it; App Percy does not.", "debug");
                return null;
            }

            object? value = Reflect.Call(buffers, GetBufferNames, name);
            // A buffer is sometimes an object wrapping the value rather than the value itself.
            return value is string text ? text : Reflect.Member(value, ValueNames)?.ToString();
        }

        /// <summary>
        /// Captures the screen by running the mobile engine's own screenshot task against this step's
        /// test action, then returns where it wrote the file.
        ///
        /// That task reads its destination from the test action's own <c>Directory</c> and
        /// <c>Filename</c> parameters — so the Percy module carries those two rows and the *same* test
        /// action is handed straight through. An earlier attempt wrapped the test action to inject the
        /// values, which would have kept the temp path out of the sheet; that is not viable, because
        /// <c>ISpecialExecutionTaskTestAction</c> has some thirty-five members and hand-implementing it
        /// against undocumented Tricentis types is far more fragile than two module rows.
        /// </summary>
        public string? CaptureScreenshot()
        {
            string? directory = Parameter("Directory");
            string? fileName = Parameter("Filename") ?? Parameter("FileName");

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                Utils.Log("App Percy needs Directory and Filename parameters on the Percy module: " +
                    $"the {_screenshotTaskName} task reads its destination from them, and this SDK " +
                    "cannot supply them on your behalf. Point them at any writable folder — the file " +
                    "is read and deleted straight away. (Percy on Automate does not need either.)");
                return null;
            }

            // Created up front: the engine may fail silently rather than create a missing folder, and
            // that failure is indistinguishable from every other "wrote no file" case.
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception e)
            {
                Utils.Log($"Cannot use '{directory}' as the screenshot folder: {e.Message}");
                return null;
            }

            List<(string Task, string Engine)> tried = new List<(string, string)>();

            foreach ((string task, string engine) in ScreenshotTaskCandidates())
            {
                if (tried.Contains((task, engine))) continue;
                tried.Add((task, engine));

                ISpecialExecutionTask? created;
                try
                {
                    created = SpecialExecutionTaskFactory.CreateTask(task, engine);
                }
                catch (Exception e)
                {
                    // Wrong name or engine for this Tosca version — that is what the candidate list
                    // exists for, so move on rather than giving up.
                    Utils.Log($"No '{task}' task under engine '{engine}': {e.Message}", "debug");
                    continue;
                }

                // Info, not debug: which task actually ran is the first thing anyone diagnosing a
                // capture needs, and requiring debug logging to learn it made a failure unreadable.
                Utils.Log($"Capturing via the '{task}' task (engine '{engine}') into {directory}.");

                // Snapshot the folder so the file can be found by what appeared, rather than by
                // guessing the name the engine chose.
                HashSet<string> before = SafeListing(directory);

                // Past this point the task exists, so a failure is a real one: do not try another
                // candidate, because the capture may have partially run.
                try
                {
                    // The real test action, unmodified: it already carries Directory and Filename, and
                    // it is also what tells the task which device to capture from.
                    created.ExecuteTask(_testAction);
                }
                catch (Exception e)
                {
                    Utils.Log($"The '{task}' task (engine '{engine}') failed: {e.Message}");
                    Utils.Log(e.ToString(), "debug");
                    return null;
                }

                string path = Path.Combine(directory, fileName);
                if (File.Exists(path)) return path;

                // Not at the expected name: take whatever appeared while the task ran. The engine may
                // add its own extension, timestamp or index, and any of those is still the screenshot.
                string? appeared = SafeListing(directory).Except(before)
                    .OrderByDescending(f => FileWriteTime(f))
                    .FirstOrDefault();
                if (appeared != null)
                {
                    Utils.Log($"The '{task}' task wrote {appeared} rather than {path}; using it.");
                    return appeared;
                }

                // Info: this is the actionable failure, and it was previously invisible without debug
                // logging — which made the whole capture path look silent.
                Utils.Log($"The '{task}' task (engine '{engine}') ran without error but wrote no file " +
                    $"to {directory}. Check that the test is steering a mobile device at this point, " +
                    "and that this folder is writable by the account running Tosca.");
                LogAvailableScreenshotTasks();
                return null;
            }

            Utils.Log("Could not find Tosca's mobile screenshot task. Tried: " +
                string.Join(", ", tried.Select(t => $"'{t.Task}'/'{t.Engine}'")) +
                ". Set ScreenshotTaskName and ScreenshotEngineId on the Percy module to the correct " +
                "pair, and run with PERCY_LOGLEVEL=debug to see every task this Tosca install offers.");
            LogAvailableScreenshotTasks();
            return null;
        }

        /// <summary>Files currently in a folder, or empty when it cannot be listed.</summary>
        private static HashSet<string> SafeListing(string directory)
        {
            try
            {
                return new HashSet<string>(Directory.EnumerateFiles(directory), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static DateTime FileWriteTime(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Task/engine pairs to try, in order: whatever the module or constructor specified, then the
        /// name Tricentis' own PrintScreen source uses, then anything this install actually registers
        /// that looks like a mobile screenshot task.
        ///
        /// Discovery is here because the documented pair is version-specific — the published
        /// "Mobile30PrintScreen"/"ME3.0" is from a Tosca 16-era page and is not registered on 24 — and
        /// hardcoding another guess would just move the problem to the next release.
        /// </summary>
        private IEnumerable<(string Task, string Engine)> ScreenshotTaskCandidates()
        {
            string? task = Parameter("ScreenshotTaskName") ?? _screenshotTaskName;
            string? engine = Parameter("ScreenshotEngineId") ?? _screenshotEngineId;
            if (!string.IsNullOrWhiteSpace(task) && !string.IsNullOrWhiteSpace(engine))
            {
                yield return (task, engine);
            }

            foreach ((string Task, string Engine) found in DiscoverScreenshotTasks())
            {
                yield return found;
            }
        }

        /// <summary>
        /// Every registered special execution task whose name looks like a screenshot, paired with the
        /// engine id of the assembly declaring it. Mobile ones are offered first.
        /// </summary>
        private static List<(string Task, string Engine)> DiscoverScreenshotTasks()
        {
            List<(string Task, string Engine, bool Mobile)> found =
                new List<(string, string, bool)>();

            foreach (Type type in TricentisTypes())
            {
                string? taskName = TaskNameOf(type);
                if (taskName == null) continue;
                if (taskName.IndexOf("printscreen", StringComparison.OrdinalIgnoreCase) < 0 &&
                    taskName.IndexOf("screenshot", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string? engineId = EngineIdOf(type.Assembly);
                if (engineId == null) continue;

                bool mobile = taskName.IndexOf("mobile", StringComparison.OrdinalIgnoreCase) >= 0
                    || engineId.IndexOf("ME", StringComparison.Ordinal) >= 0
                    || Reflect.TypeNameContains(type, "mobile");
                found.Add((taskName, engineId, mobile));
            }

            return found
                .OrderByDescending(f => f.Mobile)
                .Select(f => (f.Task, f.Engine))
                .Distinct()
                .ToList();
        }

        /// <summary>Logs every screenshot-ish task found, so a wrong guess is one log line from fixed.</summary>
        private static void LogAvailableScreenshotTasks()
        {
            List<(string Task, string Engine)> available = DiscoverScreenshotTasks();
            Utils.Log(available.Count == 0
                ? "No screenshot-like special execution tasks were found in the loaded Tricentis " +
                  "assemblies, so App Percy cannot capture on this install — use Percy on Automate."
                : "Screenshot-like tasks this install registers, for ScreenshotTaskName / " +
                  "ScreenshotEngineId: " +
                    string.Join(", ", available.Select(a => $"'{a.Task}' (engine '{a.Engine}')")));
        }

        // Read reflectively rather than by cast: the attributes' property names are not published, and
        // a wrong guess should degrade discovery rather than fail to compile.
        private static string? TaskNameOf(Type type)
        {
            object? attribute = type.GetCustomAttributes(false)
                .FirstOrDefault(a => Reflect.TypeSimpleNameContains(a, "SpecialExecutionTaskName"));
            return Reflect.Member(attribute, "Name", "SpecialExecutionTaskName", "TaskName")?.ToString();
        }

        private static string? EngineIdOf(Assembly assembly)
        {
            try
            {
                object? attribute = assembly.GetCustomAttributes(false)
                    .FirstOrDefault(a => Reflect.TypeSimpleNameContains(a, "EngineId"));
                return Reflect.Member(attribute, "Id", "EngineId", "Name")?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a module parameter, treating a blank or unreadable row as unset. Mirrors the shim's
        /// own reader; kept local so this class does not depend on the task type.
        /// </summary>
        private string? Parameter(string name)
        {
            try
            {
                string? value = _testAction.GetParameterAsInputValue(name, true)?.Value?.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads every test configuration parameter. Values arrive either as plain strings or wrapped
        /// in a parameter object, so both are unwrapped.
        /// </summary>
        private IReadOnlyDictionary<string, string?> ReadParameters()
        {
            Dictionary<string, string?> parameters = new Dictionary<string, string?>();

            object? configuration = SingletonInstance(ConfigurationTypeNames,
                candidate => Reflect.Member(candidate, ParameterMapNames) != null);
            if (configuration == null)
            {
                Utils.Log("Could not reach Tosca's test configuration parameters. Device details " +
                    "must come from the Percy module's DeviceName, OsName and OsVersion parameters.",
                    "warn");
                return parameters;
            }

            object? map = Reflect.Member(configuration, ParameterMapNames);
            IReadOnlyDictionary<string, object?>? entries = Capabilities.AsDictionary(map);
            if (entries == null)
            {
                Utils.Log("Tosca's test configuration parameters were not readable as a map.", "debug");
                Utils.Log($"{configuration.GetType().FullName} exposes: " +
                    string.Join(", ", Reflect.MemberNames(configuration)), "debug");
                return parameters;
            }

            foreach (KeyValuePair<string, object?> entry in entries)
            {
                parameters[entry.Key] = entry.Value is string text
                    ? text
                    : Reflect.Member(entry.Value, ValueNames)?.ToString();
            }
            return parameters;
        }

        /// <summary>
        /// Finds a Tricentis singleton by type name across the assemblies already loaded into Tosca
        /// Commander, and returns its <c>Instance</c> — but only if <paramref name="isUsable"/>
        /// accepts it.
        ///
        /// Searching loaded assemblies rather than naming one is deliberate: these types' assemblies
        /// have moved between Tosca releases, and by the time an extension runs, whichever assembly
        /// holds them is loaded anyway.
        ///
        /// Two things this gets right that a naive search does not. Candidate names are tried
        /// outermost, so the preference order in the arrays above is honoured — iterating assemblies
        /// first would let load order decide, and load order is not deterministic. And each candidate
        /// is checked for the member actually needed before being accepted, because "a Tricentis type
        /// named Configuration with a static Instance" describes more than one type; accepting the
        /// wrong one yields an empty parameter set and a snapshot with no device details.
        /// </summary>
        private static object? SingletonInstance(string[] typeNames, Func<object, bool> isUsable)
        {
            List<Type> candidateTypes = TricentisTypes();

            foreach (string name in typeNames)
            {
                foreach (Type type in candidateTypes)
                {
                    if (!string.Equals(type.Name, name, StringComparison.Ordinal)) continue;

                    // FlattenHierarchy: a singleton commonly inherits Instance from a
                    // Singleton<T> base, where a non-flattened lookup would not see it.
                    object? instance = null;
                    try
                    {
                        instance = type
                            .GetProperty("Instance", System.Reflection.BindingFlags.Static |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic |
                                System.Reflection.BindingFlags.FlattenHierarchy)
                            ?.GetValue(null);
                    }
                    catch (Exception e)
                    {
                        // A singleton whose initialiser needs an automation context we are not in.
                        Utils.Log($"Reading {type.FullName}.Instance failed: {e.Message}", "debug");
                    }

                    if (instance == null) continue;
                    if (!isUsable(instance))
                    {
                        Utils.Log($"{type.FullName} is not the type wanted here; continuing.", "debug");
                        continue;
                    }
                    return instance;
                }
            }
            return null;
        }

        private static List<Type>? _tricentisTypes;
        private static int _typesFromAssemblyCount = -1;

        /// <summary>
        /// Every type in the loaded Tricentis assemblies.
        ///
        /// Cached because enumerating types across ~30 Tricentis assemblies is thousands of types and
        /// this is on the path of every snapshot — but invalidated as soon as the loaded-assembly count
        /// changes, which is the part that matters. Tosca loads engine assemblies on demand, so the one
        /// holding the configuration or buffer singleton may well not be loaded when the first
        /// AppPercyScreenshot step runs. Caching a miss for the life of the process would make that miss
        /// permanent: no test configuration parameters, no session id, every snapshot degraded, and
        /// restarting Commander appearing to "fix" it.
        ///
        /// Comparing the count rather than the contents is enough here: assemblies are never unloaded,
        /// so the count only rises, and any rise is a reason to look again.
        /// </summary>
        private static List<Type> TricentisTypes()
        {
            System.Reflection.Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            if (_tricentisTypes != null && loaded.Length == _typesFromAssemblyCount)
            {
                return _tricentisTypes;
            }

            List<Type> types = new List<Type>();
            foreach (System.Reflection.Assembly assembly in loaded)
            {
                if (!(assembly.GetName().Name ?? "")
                    .StartsWith("Tricentis", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    types.AddRange(assembly.GetTypes());
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    // A partially-loadable assembly still yields the types that did load, and one of
                    // them may be the one wanted.
                    types.AddRange(e.Types.Where(t => t != null).Select(t => t!));
                }
                catch (Exception)
                {
                    // Nothing readable in this assembly; the others may still hold the type.
                }
            }
            _typesFromAssemblyCount = loaded.Length;
            return _tricentisTypes = types;
        }
    }
}
