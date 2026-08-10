using System.Reflection;
using AppPercyTosca.Core;

namespace AppPercyTosca
{
    /// <see cref="IToscaEnvironment"/> over the real Tricentis APIs: test configuration parameters and
    /// buffers, nothing else.
    ///
    /// Both are reached by reflection for a specific reason: <c>MainConfiguration</c> and
    /// <c>Buffers</c> are documented by behaviour, but their namespaces and member shapes are not
    /// published and differ across releases. Binding at compile time turns a wrong guess into "does
    /// not build on the customer's machine"; binding late turns it into a logged warning.
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

        /// Memoized per step: the map costs a reflective walk and one snapshot reads it several times.
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

        /// Values arrive as plain strings or wrapped in a parameter object; both are unwrapped.
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

        /// Finds a Tricentis singleton by type name across the loaded assemblies and returns its
        /// <c>Instance</c>, if <paramref name="isUsable"/> accepts it. Searching rather than naming an
        /// assembly, because these types have moved between releases.
        ///
        /// Two details matter. Candidate names are the outer loop, so the preference order above wins
        /// rather than nondeterministic assembly load order. And each candidate is checked for the
        /// member wanted before being accepted: more than one Tricentis type is named Configuration
        /// with a static Instance, and the wrong one yields a snapshot with no device details.
        private static object? SingletonInstance(string[] typeNames, Func<object, bool> isUsable)
        {
            List<Type> candidateTypes = TricentisTypes();

            foreach (string name in typeNames)
            {
                foreach (Type type in candidateTypes)
                {
                    if (!string.Equals(type.Name, name, StringComparison.Ordinal)) continue;

                    // FlattenHierarchy: Instance is commonly inherited from a Singleton<T> base.
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

        /// Every type in the loaded Tricentis assemblies, cached — thousands of types, on the path of
        /// every snapshot — but invalidated whenever the loaded-assembly count changes, which is the
        /// part that matters. Tosca loads engine assemblies on demand, so caching a miss for the life
        /// of the process would make it permanent: every snapshot degraded, and restarting Commander
        /// appearing to fix it.
        ///
        /// The count suffices: assemblies are never unloaded, so any rise is a reason to look again.
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
