using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class CapabilitiesTests
    {
        private static Dictionary<string, object?> Caps(params (string Key, object? Value)[] entries) =>
            entries.ToDictionary(e => e.Key, e => e.Value);

        [Fact]
        public void RawPrefersTheTopLevelKey()
        {
            Dictionary<string, object?> caps = Caps(
                ("deviceName", "top"),
                ("bstack:options", new Dictionary<string, object?> { ["deviceName"] = "nested" }));

            Assert.Equal("top", caps.GetString("deviceName"));
        }

        [Theory]
        [InlineData("bstack:options")]
        [InlineData("appium:options")]
        [InlineData("desired")]
        public void RawFallsBackToTheVendorContainers(string container)
        {
            // A W3C session nests vendor capabilities rather than flattening them, so a key that
            // is absent at the top level may still be present one level down.
            Dictionary<string, object?> caps = Caps(
                (container, new Dictionary<string, object?> { ["deviceName"] = "Pixel 7" }));

            Assert.Equal("Pixel 7", caps.GetString("deviceName"));
        }

        [Fact]
        public void RawIgnoresNullValuesAndUnknownKeys()
        {
            Dictionary<string, object?> caps = Caps(("deviceName", null));
            Assert.Null(caps.GetString("deviceName"));
            Assert.Null(caps.GetString("nothing"));
        }

        [Fact]
        public void RawIgnoresAVendorContainerThatIsNotObjectShaped()
        {
            Dictionary<string, object?> caps = Caps(("bstack:options", "not-a-map"));
            Assert.Null(caps.GetString("deviceName"));
        }

        [Fact]
        public void GetStringRendersNonStringValues()
        {
            Assert.Equal("13", Caps(("platformVersion", 13)).GetString("platformVersion"));
            Assert.Equal("True", Caps(("flag", true)).GetString("flag"));
        }

        [Theory]
        [InlineData(1080, 1080)]
        [InlineData(1080L, 1080)]
        [InlineData(1080.9, 1080)]
        [InlineData("1080", 1080)]
        [InlineData("1080.9", 1080)]
        public void GetIntCoercesTheShapesCapabilitiesArriveIn(object value, int expected)
        {
            Assert.Equal(expected, Caps(("width", value)).GetInt("width"));
        }

        [Fact]
        public void GetIntCoercesTheRemainingNumericWidths()
        {
            Assert.Equal(7, Capabilities.ToInt((short)7));
            Assert.Equal(7, Capabilities.ToInt(7f));
            Assert.Equal(7, Capabilities.ToInt(7m));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wide")]
        [InlineData(true)]
        public void GetIntReturnsNullForValuesThatAreNotNumbers(object? value)
        {
            Assert.Null(Capabilities.ToInt(value));
        }

        [Fact]
        public void GetIntReturnsNullForAValueOfAnUnexpectedType()
        {
            // Capabilities are loosely typed on the wire; an unanticipated shape must read as
            // "not a number", not throw out of a metadata lookup.
            Assert.Null(Capabilities.ToInt(new object()));
            Assert.Null(Capabilities.ToInt(new List<int> { 1 }));
        }

        [Fact]
        public void GetIntParsesWithInvariantCultureNotTheWorkstationLocale()
        {
            // A Tosca workstation set to a comma-decimal locale must still read "1080.9" the same
            // way; parsing with the current culture would fail or read it as 10809.
            System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                Assert.Equal(1080, Caps(("width", "1080.9")).GetInt("width"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void GetMapReadsNestedObjects()
        {
            Dictionary<string, object?> caps = Caps(
                ("percyOptions", new Dictionary<string, object?> { ["enabled"] = false }));

            Assert.Equal(false, caps.GetMap("percyOptions")!["enabled"]);
            Assert.Null(caps.GetMap("missing"));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData("false", true)]
        [InlineData("FALSE", true)]
        [InlineData(true, false)]
        [InlineData("true", false)]
        [InlineData(null, false)]
        [InlineData(0, false)]
        public void IsFalseOnlyTreatsAnExplicitFalseAsFalse(object? value, bool expected)
        {
            // A missing capability is not "false": Percy is opt-out, so absent means enabled.
            Assert.Equal(expected, Capabilities.IsFalse(value));
        }

        [Fact]
        public void AsDictionaryNormalizesTheShapesANestedCapabilityArrivesIn()
        {
            Assert.Null(Capabilities.AsDictionary(null));
            Assert.Null(Capabilities.AsDictionary(42));

            Dictionary<string, object?> nullable = new Dictionary<string, object?> { ["a"] = 1 };
            Assert.Equal(1, Capabilities.AsDictionary(nullable)!["a"]);

            // The non-nullable spelling, which is what most automation clients hand back.
            Dictionary<string, object> plain = new Dictionary<string, object> { ["a"] = 1 };
            Assert.Equal(1, Capabilities.AsDictionary(plain)!["a"]);

            // A parsed JsonElement, which is how capabilities arrive when read off the wire.
            Assert.Equal(1L, Capabilities.AsDictionary(Json.TryParse("{\"a\":1}")!.Value)!["a"]);
            Assert.Null(Capabilities.AsDictionary(Json.TryParse("[1]")!.Value));
        }

        [Fact]
        public void AsDictionaryDropsNonStringKeys()
        {
            System.Collections.Hashtable table = new System.Collections.Hashtable
            {
                ["a"] = 1,
                [7] = 2
            };
            IReadOnlyDictionary<string, object?> converted = Capabilities.AsDictionary(table)!;
            Assert.Equal(1, converted["a"]);
            Assert.Single(converted);
        }

        [Fact]
        public void AsDictionaryAcceptsAMapThatIsOnlyWriteableGeneric()
        {
            // Most BCL dictionaries implement both interfaces, but a custom map handed back by an
            // automation client need not — so the write-only-generic arm is not redundant.
            Assert.Equal(1, Capabilities.AsDictionary(new GenericOnlyMap { ["a"] = 1 })!["a"]);
        }

        [Fact]
        public void AsDictionaryPassesThroughAReadOnlyDictionaryUnchanged()
        {
            IReadOnlyDictionary<string, object?> source =
                new Dictionary<string, object?> { ["a"] = 1 };
            Assert.Same(source, Capabilities.AsDictionary(source));
        }

        /// <summary>
        /// Implements <see cref="IDictionary{TKey, TValue}"/> but deliberately not
        /// <see cref="IReadOnlyDictionary{TKey, TValue}"/>, which no BCL dictionary does.
        /// </summary>
        private class GenericOnlyMap : IDictionary<string, object?>
        {
            private readonly Dictionary<string, object?> _inner = new Dictionary<string, object?>();

            public object? this[string key] { get => _inner[key]; set => _inner[key] = value; }
            public ICollection<string> Keys => _inner.Keys;
            public ICollection<object?> Values => _inner.Values;
            public int Count => _inner.Count;
            public bool IsReadOnly => false;
            public void Add(string key, object? value) => _inner.Add(key, value);
            public void Add(KeyValuePair<string, object?> item) => _inner.Add(item.Key, item.Value);
            public void Clear() => _inner.Clear();
            public bool Contains(KeyValuePair<string, object?> item) => _inner.Contains(item);
            public bool ContainsKey(string key) => _inner.ContainsKey(key);
            public void CopyTo(KeyValuePair<string, object?>[] array, int index) =>
                ((ICollection<KeyValuePair<string, object?>>)_inner).CopyTo(array, index);
            public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _inner.GetEnumerator();
            public bool Remove(string key) => _inner.Remove(key);
            public bool Remove(KeyValuePair<string, object?> item) =>
                ((ICollection<KeyValuePair<string, object?>>)_inner).Remove(item);
            public bool TryGetValue(string key, out object? value) => _inner.TryGetValue(key, out value);
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                _inner.GetEnumerator();
        }
    }
}
