using System.Reflection;

namespace AppPercyTosca.Core
{
    /// Late-bound member access, used by the shim to reach Tosca's configuration and buffer
    /// singletons: their shapes are not a documented contract and differ across releases, so
    /// compiling against them is not safe.
    ///
    /// Every lookup takes candidate names and returns null on a miss rather than throwing, so one
    /// renamed member degrades a single field instead of the whole snapshot.
    public static class Reflect
    {
        private const BindingFlags Instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// Reads the first readable property or field matching one of <paramref name="names"/>,
        /// searching the whole type hierarchy. Returns null when none is found or the read threw.
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

        /// Invokes the first method matching one of <paramref name="names"/> whose parameter count
        /// matches <paramref name="args"/>. Returns null when none is found or the call threw.
        public static object? Call(object? target, string[] names, params object?[] args)
        {
            if (target == null) return null;

            foreach (string name in names)
            {
                MethodInfo? method = FindMethod(target.GetType(), name, args.Length);
                if (method == null) continue;

                object? value = Read(() => method.Invoke(target, args), target, name);
                // Methods are matched on arity alone, so the first match may be the wrong overload
                // and throw. Returning its null would abandon the remaining candidates — a
                // GetBuffer(Guid) in front of a GetBufferValue(string) would fail every buffer read.
                if (value != null) return value;
            }
            return null;
        }

        /// Every property and field name on the object, for the diagnostic dump.
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

        // Walked explicitly: GetProperty/GetField with NonPublic only see members declared on the
        // exact type, so a private field inherited from a base class would be invisible.
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

        /// Any failure becomes null plus a debug line: a Tricentis singleton can throw when the
        /// automation context is not initialised, and that must not become a failed snapshot.
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
