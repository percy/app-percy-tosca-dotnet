using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    /// <summary>
    /// Objects shaped like the Tricentis singletons the shim reaches by reflection — a
    /// configuration holder whose real map is a private field, and a nested wrapper — so the
    /// late-binding rules can be pinned down without Tosca installed.
    /// </summary>
    public static class FakeToscaTypes
    {
        public class ConfigurationParameter
        {
            public string? Value { get; set; }
        }

        /// <summary>Mirrors a Tricentis singleton: an Instance property holding the real object.</summary>
        public class MainConfiguration
        {
            public static MainConfiguration Instance { get; } = new MainConfiguration();

            public Dictionary<string, ConfigurationParameter> LoadedTestConfigurationParameter { get; } =
                new Dictionary<string, ConfigurationParameter>
                {
                    ["AppiumServer"] = new ConfigurationParameter { Value = "https://hub.example.com/wd/hub" }
                };
        }

        public class BaseHolder
        {
            private readonly string secret = "found-me";
        }

        public class DerivedHolder : BaseHolder
        {
            public string? Empty { get; set; }

#pragma warning disable CS0649 // never assigned on purpose: an unpopulated field
            public string? emptyField;
#pragma warning restore CS0649

            public string Present => "value";
        }
    }

    public class ReflectTests : CoreTestBase
    {
        [Fact]
        public void MembersAreFoundByAnyOfTheirCandidateNames()
        {
            FakeToscaTypes.DerivedHolder holder = new FakeToscaTypes.DerivedHolder();

            Assert.Equal("value", Reflect.Member(holder, "Nope", "Present"));
            Assert.Null(Reflect.Member(holder, "NotThere"));
            Assert.Null(Reflect.Member(null, "Present"));
        }

        [Fact]
        public void PrivateFieldsInheritedFromABaseClassAreFound()
        {
            // GetField with NonPublic only returns members declared on the exact type, so without
            // walking the hierarchy anything a Tricentis type keeps on a base class is invisible.
            Assert.Equal("found-me", Reflect.Member(new FakeToscaTypes.DerivedHolder(), "secret"));
        }

        [Theory]
        [InlineData("Empty")]      // an unpopulated property
        [InlineData("emptyField")] // an unpopulated field
        public void AMemberThatExistsButHoldsNullDoesNotStopTheSearch(string emptyMember)
        {
            // A Tricentis object commonly declares a member it has not populated. Treating "found"
            // as "done" would return null and skip the candidate that actually has the value — and
            // both the property and the field lookup have to behave that way.
            Assert.Equal("value",
                Reflect.Member(new FakeToscaTypes.DerivedHolder(), emptyMember, "Present"));
        }

        [Fact]
        public void MethodsAreInvokedByAnyOfTheirCandidateNames()
        {
            Assert.Equal("ran", Reflect.Call(new Callable(), new[] { "Nope", "Run" }));
            Assert.Null(Reflect.Call(new Callable(), new[] { "NotThere" }));
            Assert.Null(Reflect.Call(null, new[] { "Run" }));
        }

        [Fact]
        public void OnlyAMethodWithAMatchingParameterCountIsInvoked()
        {
            Callable target = new Callable();

            Assert.Equal("ran: x", Reflect.Call(target, new[] { "Echo" }, "x"));
            // Wrong arity must not bind, rather than throwing a binding exception at the caller.
            Assert.Null(Reflect.Call(target, new[] { "Echo" }));
        }

        [Fact]
        public void AVoidMethodIsNotTreatedAsAReadableMember()
        {
            // Reflect only exists to read values; matching a void method would return null and stop
            // the search at a candidate that produced nothing.
            Assert.Null(Reflect.Call(new Callable(), new[] { "DoNothing" }));
        }

        [Fact]
        public void APathWalksThroughBothMethodsAndProperties()
        {
            // A Tricentis chain mixes the two — and which link is which is not knowable without the
            // assemblies, so every link tries both.
            Assert.Equal("value", Reflect.Path(new Nested(), "Inner", "Deeper", "Present"));
            Assert.Equal("value", Reflect.Path(new Nested(), "GetInner", "Deeper", "Present"));
        }

        [Fact]
        public void APathReachesTheTricentisSingletonPattern()
        {
            // Type.Instance.LoadedTestConfigurationParameter is exactly the shape the shim walks to
            // reach the test configuration parameters.
            object? parameters = Reflect.Path(FakeToscaTypes.MainConfiguration.Instance,
                "LoadedTestConfigurationParameter");

            IReadOnlyDictionary<string, object?> map = Capabilities.AsDictionary(parameters)!;
            Assert.Equal("https://hub.example.com/wd/hub",
                Reflect.Member(map["AppiumServer"], "Value"));
        }

        [Fact]
        public void APathStopsAtTheFirstMissingLink()
        {
            Assert.Null(Reflect.Path(new Nested(), "Inner", "Nope", "Present"));
            Assert.Null(Reflect.Path(null, "Inner"));
        }

        [Fact]
        public void AMemberThatThrowsReadsAsAbsentAndIsLogged()
        {
            // A Tricentis singleton can throw when the automation context is not initialised; that
            // must degrade one field, not fail the snapshot.
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Null(Reflect.Member(new Hostile(), "Boom"));
            Assert.True(Logged("no context"));
        }

        [Fact]
        public void AMethodThatThrowsAlsoReadsAsAbsentAndUnwrapsTheRealCause()
        {
            // Invoke() wraps the real exception in a TargetInvocationException, whose own message
            // says nothing useful — "Exception has been thrown by the target of an invocation."
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Null(Reflect.Call(new Hostile(), new[] { "Explode" }));
            Assert.True(Logged("engine not started"));
            Assert.False(Logged("target of an invocation"));
        }

        [Fact]
        public void TypeNamesAreMatchedThroughTheHierarchy()
        {
            FakeToscaTypes.DerivedHolder holder = new FakeToscaTypes.DerivedHolder();

            Assert.True(Reflect.TypeNameContains(holder, "DerivedHolder"));
            Assert.True(Reflect.TypeNameContains(holder, "BaseHolder"));
            Assert.True(Reflect.TypeNameContains(holder, "baseholder"));
            Assert.False(Reflect.TypeNameContains(holder, "Nope"));
            Assert.False(Reflect.TypeNameContains(null, "x"));
        }

        [Fact]
        public void TheQualifiedMatchSeesTheNamespaceButTheSimpleOneDoesNot()
        {
            // Platform detection wants the namespace, since an Appium driver names its platform
            // there. Heuristics over unknown objects must not see it, or every type in a namespace
            // inherits that namespace's name.
            FakeToscaTypes.DerivedHolder holder = new FakeToscaTypes.DerivedHolder();

            Assert.True(Reflect.TypeNameContains(holder, "FakeToscaTypes"));
            Assert.False(Reflect.TypeSimpleNameContains(holder, "FakeToscaTypes"));
            Assert.True(Reflect.TypeSimpleNameContains(holder, "BaseHolder"));
            Assert.False(Reflect.TypeSimpleNameContains(null, "x"));
        }

        [Fact]
        public void MemberNamesListWhatIsAvailableForDiagnostics()
        {
            IEnumerable<string> names = Reflect.MemberNames(new FakeToscaTypes.DerivedHolder());

            Assert.Contains("Present", names);
            Assert.Contains("emptyField", names);
            // A private field on a base class: the dump is for working out why a lookup missed, so
            // it has to show the same members a lookup would have searched.
            Assert.Contains("secret", names);
            // An inherited object member proves the walk reaches base types.
            Assert.Contains("ToString()", names);
            // Property getters are special-named and must not be listed as methods.
            Assert.DoesNotContain("get_Present()", names);
            Assert.Empty(Reflect.MemberNames(null));
        }

        private class Callable
        {
            public string Present => "value";
            public string Run() => "ran";
            public string Echo(string value) => "ran: " + value;
            public void DoNothing() { }
        }

        private class Nested
        {
            private readonly Middle _inner = new Middle();

            public Middle Inner => _inner;

            /// <summary>The same link reachable as a method, to prove both spellings are tried.</summary>
            public Middle GetInner() => _inner;

            internal class Middle
            {
                public FakeToscaTypes.DerivedHolder Deeper { get; } = new FakeToscaTypes.DerivedHolder();
            }
        }

        private class Hostile
        {
            public string Boom => throw new InvalidOperationException("no context");
            public string Explode() => throw new InvalidOperationException("engine not started");
        }
    }
}
