using System.Reflection;
using AppPercyTosca.Core;

namespace AppPercyTosca
{
    /// <summary>
    /// <see cref="IToscaEnvironment"/> over the real Tricentis APIs: test configuration parameters and
    /// buffers, and nothing else. Capture and scripting go straight to the session over HTTP.
    ///
    /// Both are reached by reflection rather than a direct call, and the reason is specific rather than
    /// general timidity: <c>MainConfiguration</c> and <c>Buffers</c> are documented by behaviour but
    /// their namespaces and exact member shapes are not published, and they differ across Tosca
    /// releases. Binding to a guess at compile time turns a wrong guess into "does not build on the
    /// customer's machine"; binding late turns it into a logged warning and a degraded snapshot.
    /// </summary>
    internal class ToscaEnvironment : IToscaEnvironment
    {
        // Members are probed under several names because these types' shapes are not published.
        private static readonly string[] ConfigurationTypeNames =
            { "MainConfiguration", "Configuration", "TestConfiguration" };
        private static readonly string[] ParameterMapNames =
            { "LoadedTestConfigurationParameter", "LoadedTestConfigurationParameters",
              "TestConfigurationParameters" };
        private static readonly string[] BuffersTypeNames = { "Buffers", "BufferManager", "Buffer" };
        private static readonly string[] GetBufferNames = { "GetBuffer", "GetBufferValue", "Get" };
        private static readonly string[] ValueNames = { "Value", "ValueAsString", "UnresolvedValue" };

        /// <summary>
        /// Memoized per step: the parameter map costs a reflective walk, and one snapshot reads
        /// several values out of it.
        /// </summary>
        private IReadOnlyDictionary<string, string?>? _parameters;

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
                    "available and the device cannot be captured directly.", "debug");
                return null;
            }

            object? value = Reflect.Call(buffers, GetBufferNames, name);
            // A buffer is sometimes an object wrapping the value rather than the value itself.
            return value is string text ? text : Reflect.Member(value, ValueNames)?.ToString();
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
                Utils.Log("Could not reach Tosca's test configuration parameters, so the AppiumServer " +
                    "address is not available and the device session cannot be reached. Device " +
                    "details are read from that session, so the snapshot will be tagged with " +
                    "nothing identifying the device.", "warn");
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
