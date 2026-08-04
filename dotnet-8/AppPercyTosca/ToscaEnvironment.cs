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
            object? buffers = SingletonInstance(BuffersTypeNames);
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
        /// The task takes its destination from the test action's <c>Directory</c> and
        /// <c>Filename</c> parameters, which a Percy module does not have — so the values are pushed
        /// in via a wrapper around the test action that answers those two names itself and delegates
        /// everything else. That is the whole reason <see cref="ScreenshotTestAction"/> exists.
        /// </summary>
        public string? CaptureScreenshot(string directory, string fileName)
        {
            try
            {
                ISpecialExecutionTask task = SpecialExecutionTaskFactory.CreateTask(
                    _screenshotTaskName, _screenshotEngineId);

                task.ExecuteTask(new ScreenshotTestAction(_testAction, directory, fileName));

                string path = Path.Combine(directory, fileName);
                if (File.Exists(path)) return path;

                // The engine may have appended its own extension, so accept a near match rather
                // than reporting failure for a file that is right there.
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
        /// Reads every test configuration parameter. Values arrive either as plain strings or wrapped
        /// in a parameter object, so both are unwrapped.
        /// </summary>
        private IReadOnlyDictionary<string, string?> ReadParameters()
        {
            Dictionary<string, string?> parameters = new Dictionary<string, string?>();

            object? configuration = SingletonInstance(ConfigurationTypeNames);
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
        /// Commander, and returns its <c>Instance</c>.
        ///
        /// Searching loaded assemblies rather than naming one is deliberate: these types' assemblies
        /// have moved between Tosca releases, and by the time an extension runs, whichever assembly
        /// holds them is loaded anyway.
        /// </summary>
        private static object? SingletonInstance(string[] typeNames)
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!(assembly.FullName ?? "").StartsWith("Tricentis", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    // A partially-loadable assembly still yields the types that did load, and one of
                    // them may be the one wanted.
                    types = e.Types.Where(t => t != null).Select(t => t!).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string name in typeNames)
                {
                    Type? type = types.FirstOrDefault(t =>
                        string.Equals(t.Name, name, StringComparison.Ordinal));
                    if (type == null) continue;

                    object? instance = type
                        .GetProperty("Instance", System.Reflection.BindingFlags.Static |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic)
                        ?.GetValue(null);
                    if (instance != null) return instance;
                }
            }
            return null;
        }
    }
}
