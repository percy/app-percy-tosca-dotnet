using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    /// Objects shaped like the Tricentis singletons the shim reaches by reflection — a
    /// configuration holder whose real map is a private field, and a nested wrapper — so the
    /// late-binding rules can be pinned down without Tosca installed.
    public static class FakeToscaTypes
    {
        public class ConfigurationParameter
        {
            public string? Value { get; set; }
        }

        /// Mirrors a Tricentis singleton: an Instance property holding the real object.
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
            // Unused by design: reaching a private field on a base class is exactly what the test
            // below checks Reflect can do, so these are describing the fixture, not a leftover.
            // Both spellings are needed — IDE0051 is the analyzer's, CS0414 the compiler's, and
            // suppressing only the first left a warning annotation on every pull request.
#pragma warning disable IDE0051, CS0414
            private readonly string secret = "found-me";
#pragma warning restore IDE0051, CS0414
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
        public void AFailedInvokeFallsThroughToTheNextCandidateName()
        {
            // Methods are matched on arity alone — parameter types are unknowable here — so the first
            // match may be the wrong overload and throw. Returning its null would abandon the
            // remaining candidates: a GetBuffer(Guid) in front of a GetBufferValue(string) would make
            // every buffer read fail while reporting the buffer as unset.
            SetEnv("PERCY_LOGLEVEL", "debug");

            Assert.Equal("value: k",
                Reflect.Call(new WrongOverloadFirst(), new[] { "GetBuffer", "GetBufferValue" }, "k"));
            Assert.True(Logged("wrong type"));
        }

        [Fact]
        public void AVoidMethodIsNotTreatedAsAReadableMember()
        {
            // Reflect only exists to read values; matching a void method would return null and stop
            // the search at a candidate that produced nothing.
            Assert.Null(Reflect.Call(new Callable(), new[] { "DoNothing" }));
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

            /// The same link reachable as a method, to prove both spellings are tried.
            public Middle GetInner() => _inner;

            internal class Middle
            {
                public FakeToscaTypes.DerivedHolder Deeper { get; } = new FakeToscaTypes.DerivedHolder();
            }
        }

        /// Shaped like the failure mode above: the preferred name exists but throws for the argument
        /// given, and the usable member is a later candidate.
        private class WrongOverloadFirst
        {
            public string GetBuffer(object key) =>
                throw new ArgumentException("wrong type for this overload");

            public string GetBufferValue(string key) => "value: " + key;
        }

        private class Hostile
        {
            public string Boom => throw new InvalidOperationException("no context");
            public string Explode() => throw new InvalidOperationException("engine not started");
        }
    }
}
