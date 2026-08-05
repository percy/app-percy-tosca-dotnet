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

            try
            {
                ISpecialExecutionTask task = SpecialExecutionTaskFactory.CreateTask(
                    _screenshotTaskName, _screenshotEngineId);

                // The real test action, unmodified: it already carries Directory and Filename, and it
                // is also what tells the task which device to capture from.
                task.ExecuteTask(_testAction);

                string path = Path.Combine(directory, fileName);
                if (File.Exists(path)) return path;

                // The engine may have appended its own extension, so accept a near match rather than
                // reporting failure for a file that is right there.
                string? match = Directory.EnumerateFiles(directory,
                        Path.GetFileNameWithoutExtension(fileName) + ".*")
                    .FirstOrDefault();
                if (match != null) return match;

                Utils.Log($"The {_screenshotTaskName} task ran but wrote no file to {path}.", "debug");
                return null;
            }
            catch (Exception e)
            {
                Utils.Log($"Could not capture a screenshot through the {_screenshotTaskName} task: " +
                    e.Message);
                Utils.Log(e.ToString(), "debug");
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
