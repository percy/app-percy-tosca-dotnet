using System.Reflection;

namespace AppPercyTosca.Core
{
    /// <summary>
    /// Looks for an automation driver object among the types loaded into the host process.
    ///
    /// Why this exists rather than a settled answer: this SDK captures over HTTP because Tricentis
    /// documents Mobile Engine 3.0 as running out of process, which would mean no driver object to
    /// borrow. That is documentation plus inference, not proof — and the difference matters, because a
    /// reachable driver would enable full-page capture through <c>browserstack_executor</c>, which the
    /// HTTP route cannot do. <see cref="GenericProvider"/> and <see cref="AppAutomate"/> are already
    /// written against <see cref="IMobileDriver"/>, so the only missing piece would be
    /// <c>ExecuteScript</c>.
    ///
    /// So this reports what is actually in the process, to be read from the Diagnose output while a
    /// mobile test is live. It only ever looks — nothing here is used to capture.
    /// </summary>
    public static class DriverProbe
    {
        /// <summary>What a candidate offers, in the order of how much it would unlock.</summary>
        public class Candidate
        {
            public string TypeName { get; init; } = "";
            public bool CanScreenshot { get; init; }
            public bool CanExecuteScript { get; init; }
            public bool HasSessionId { get; init; }

            /// <summary>Name of a static member that hands back an instance, when one exists.</summary>
            public string? StaticAccessor { get; init; }

            /// <summary>
            /// How promising this is. Weighted so scripting counts most: a screenshot is already
            /// obtainable over HTTP, whereas scripting is the capability that is missing entirely.
            /// </summary>
            public int Score =>
                (CanExecuteScript ? 4 : 0) + (CanScreenshot ? 2 : 0) +
                (HasSessionId ? 1 : 0) + (StaticAccessor != null ? 2 : 0);

            public override string ToString()
            {
                List<string> has = new List<string>();
                if (CanExecuteScript) has.Add("ExecuteScript");
                if (CanScreenshot) has.Add("GetScreenshot");
                if (HasSessionId) has.Add("SessionId");
                if (StaticAccessor != null) has.Add($"static {StaticAccessor}");
                return $"{TypeName} [{string.Join(", ", has)}]";
            }
        }

        private static readonly string[] ScreenshotNames =
            { "GetScreenshot", "TakeScreenshot", "GetScreenshotAsBase64", "CaptureScreenshot" };
        private static readonly string[] ScriptNames =
            { "ExecuteScript", "ExecuteDriverScript", "ExecuteAsyncScript" };
        private static readonly string[] SessionNames = { "SessionId", "SessionID", "SessionGuid" };
        private static readonly string[] AccessorNames =
            { "Instance", "Current", "CurrentSession", "CurrentDriver", "Driver", "Session" };

        private const BindingFlags Instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Reads a type's methods. A seam only so the guard below can be tested: scanning every type in
        /// a host process genuinely does hit types whose members cannot be reflected over, because
        /// their dependencies are not present.
        /// </summary>
        internal static Func<Type, MethodInfo[]> MethodsOf { get; set; } =
            type => type.GetMethods(Instance);

        /// <summary>
        /// Scores every type that looks like a driver, best first. Types are passed in rather than
        /// discovered here so this is testable without a host process.
        /// </summary>
        public static List<Candidate> Find(IEnumerable<Type> types)
        {
            List<Candidate> candidates = new List<Candidate>();

            foreach (Type type in types)
            {
                try
                {
                    Candidate? candidate = Inspect(type);
                    if (candidate != null) candidates.Add(candidate);
                }
                catch (Exception)
                {
                    // A type whose members cannot be reflected over is not a candidate, and must not
                    // stop the scan — one unloadable type among thousands is the normal case.
                }
            }

            return candidates.OrderByDescending(c => c.Score).ToList();
        }

        private static Candidate? Inspect(Type type)
        {
            MethodInfo[] methods = MethodsOf(type);

            bool screenshot = methods.Any(m => ScreenshotNames.Contains(m.Name));
            bool script = methods.Any(m => ScriptNames.Contains(m.Name));
            if (!screenshot && !script) return null;

            return new Candidate
            {
                TypeName = type.FullName ?? type.Name,
                CanScreenshot = screenshot,
                CanExecuteScript = script,
                HasSessionId = HasAnyMember(type, SessionNames),
                StaticAccessor = StaticAccessorOn(type)
            };
        }

        /// <summary>
        /// A one-line verdict for the Diagnose output, plus the candidates worth trying. Kept short on
        /// purpose: the full list can be hundreds of types and the useful part is whether *anything*
        /// offers scripting.
        /// </summary>
        public static string Describe(IEnumerable<Type> types, int limit = 8)
        {
            List<Candidate> candidates = Find(types);
            if (candidates.Count == 0)
            {
                return "driver probe: nothing in this process exposes a screenshot or script method, " +
                    "so there is no driver object to borrow — capture over HTTP is the only route.";
            }

            List<Candidate> scriptable = candidates.Where(c => c.CanExecuteScript).ToList();
            string verdict = scriptable.Count == 0
                ? "driver probe: candidates found, but none can execute scripts — so full page capture " +
                  "is still out of reach."
                : $"driver probe: {scriptable.Count} candidate(s) can execute scripts, which is what " +
                  "full page capture needs. Worth pursuing.";

            return verdict + Environment.NewLine +
                string.Join(Environment.NewLine,
                    candidates.Take(limit).Select(c => "  " + c));
        }

        // No try/catch here or below: both are only reached from Inspect, which is guarded.
        private static bool HasAnyMember(Type type, string[] names) =>
            names.Any(n => type.GetProperty(n, Instance) != null || type.GetField(n, Instance) != null);

        /// <summary>
        /// A static member handing back an instance of this type. That is the difference between
        /// "such a type exists" and "an instance can actually be reached from an extension".
        /// </summary>
        private static string? StaticAccessorOn(Type type)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic;
            foreach (string name in AccessorNames)
            {
                PropertyInfo? property = type.GetProperty(name, flags);
                if (property != null && type.IsAssignableFrom(property.PropertyType)) return name;

                FieldInfo? field = type.GetField(name, flags);
                if (field != null && type.IsAssignableFrom(field.FieldType)) return name;
            }
            return null;
        }
    }
}
