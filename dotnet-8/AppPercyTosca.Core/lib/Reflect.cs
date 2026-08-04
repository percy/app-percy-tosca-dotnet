using System.Reflection;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Late-bound member access. Everything the Core needs from a Tosca mobile session has to be
    /// reached this way: the Tricentis assemblies are only present on a machine with Tosca
    /// installed, and the Mobile engine's session types are not part of a documented extension
    /// contract, so compiling against them is neither possible here nor safe across Tosca releases.
    ///
    /// Every lookup takes a list of candidate names and returns null on a miss rather than
    /// throwing, so one renamed member degrades a single field instead of the whole snapshot.
    /// </summary>
    public static class Reflect
    {
        private const BindingFlags Instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Reads the first readable property or field matching one of <paramref name="names"/>,
        /// searching the whole type hierarchy. Returns null when none is found or the read threw.
        /// </summary>
        public static object? Member(object? target, params string[] names)
        {
            if (target == null) return null;

            foreach (string name in names)
            {
                PropertyInfo? property = FindProperty(target.GetType(), name);
                if (property != null)
                {
                    object? value = Read(() => property.GetValue(target), target, name);
                    if (value != null) return value;
                }

                FieldInfo? field = FindField(target.GetType(), name);
                if (field != null)
                {
                    object? value = Read(() => field.GetValue(target), target, name);
                    if (value != null) return value;
                }
            }
            return null;
        }

        /// <summary>
        /// Invokes the first method matching one of <paramref name="names"/> whose parameter count
        /// matches <paramref name="args"/>. Returns null when none is found or the call threw.
        /// </summary>
        public static object? Call(object? target, string[] names, params object?[] args)
        {
            if (target == null) return null;

            foreach (string name in names)
            {
                MethodInfo? method = FindMethod(target.GetType(), name, args.Length);
                if (method == null) continue;
                return Read(() => method.Invoke(target, args), target, name);
            }
            return null;
        }

        /// <summary>
        /// Reads a chain of members, e.g. Manage -> Window -> Size -> Width. Stops and returns null
        /// at the first missing link.
        /// </summary>
        public static object? Path(object? target, params string[] names)
        {
            object? current = target;
            foreach (string name in names)
            {
                if (current == null) return null;
                // Some links in these chains are methods (Manage()) and some are properties
                // (Window), and which is which differs between client versions.
                current = Member(current, name) ?? Call(current, new[] { name });
            }
            return current;
        }

        /// <summary>
        /// True when the namespace-qualified name of <paramref name="target"/>'s type, or of any
        /// base type, contains <paramref name="fragment"/>. The namespace is included on purpose:
        /// an Appium driver announces its platform there (OpenQA.Selenium.Appium.Android) as much as
        /// in its class name.
        /// </summary>
        public static bool TypeNameContains(object? target, string fragment) =>
            AnyTypeInHierarchy(target, type => type.FullName != null &&
                type.FullName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// True when the bare class name of <paramref name="target"/>'s type, or of any base type,
        /// contains <paramref name="fragment"/> — ignoring the namespace and any enclosing type.
        ///
        /// Use this for heuristics over an unknown object graph. Matching the qualified name there
        /// tars every type in a namespace such as Tricentis.Automation.Mobile30.SessionManagement
        /// with whatever that namespace happens to be called.
        /// </summary>
        public static bool TypeSimpleNameContains(object? target, string fragment) =>
            AnyTypeInHierarchy(target, type =>
                type.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        private static bool AnyTypeInHierarchy(object? target, Func<Type, bool> predicate)
        {
            Type? type = target?.GetType();
            while (type != null)
            {
                if (predicate(type)) return true;
                type = type.BaseType;
            }
            return false;
        }

        /// <summary>Every property and field name on the object, for the diagnostic dump.</summary>
        public static IEnumerable<string> MemberNames(object? target)
        {
            Type? type = target?.GetType();
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            while (type != null)
            {
                foreach (PropertyInfo property in type.GetProperties(Instance | BindingFlags.DeclaredOnly))
                    names.Add(property.Name);
                foreach (FieldInfo field in type.GetFields(Instance | BindingFlags.DeclaredOnly))
                    names.Add(field.Name);
                foreach (MethodInfo method in type.GetMethods(Instance | BindingFlags.DeclaredOnly))
                    if (!method.IsSpecialName) names.Add(method.Name + "()");
                type = type.BaseType;
            }
            return names.OrderBy(n => n, StringComparer.Ordinal);
        }

        // Walks the hierarchy explicitly: GetProperty/GetField with NonPublic only return members
        // declared on the exact type, so a private field inherited from a base class — which is
        // where automation clients keep the server URI — is invisible without this.
        private static PropertyInfo? FindProperty(Type? type, string name)
        {
            while (type != null)
            {
                PropertyInfo? property = type.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    return property;
                }
                type = type.BaseType;
            }
            return null;
        }

        private static FieldInfo? FindField(Type? type, string name)
        {
            while (type != null)
            {
                FieldInfo? field = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        private static MethodInfo? FindMethod(Type? type, string name, int argCount)
        {
            while (type != null)
            {
                MethodInfo? match = type
                    .GetMethods(Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == name &&
                        m.GetParameters().Length == argCount &&
                        m.ReturnType != typeof(void));
                if (match != null) return match;
                type = type.BaseType;
            }
            return null;
        }

        /// <summary>
        /// Runs a reflective read, converting any failure into null plus a debug line. A property
        /// on a live session can throw for ordinary reasons — the app moved on, the device
        /// disconnected — and that must not become a failed snapshot.
        /// </summary>
        private static object? Read(Func<object?> read, object target, string name)
        {
            try
            {
                return read();
            }
            catch (Exception e)
            {
                Utils.Log($"Reading {target.GetType().Name}.{name} failed: " +
                    (e is TargetInvocationException invocation && invocation.InnerException != null
                        ? invocation.InnerException.Message
                        : e.Message), "debug");
                return null;
            }
        }
    }
}
