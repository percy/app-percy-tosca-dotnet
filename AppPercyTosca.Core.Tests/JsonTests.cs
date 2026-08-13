using System.Text.Json;
using AppPercyTosca.Core;
using Xunit;

namespace AppPercyTosca.Core.Tests
{
    public class JsonTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json")]
        [InlineData("{\"unclosed\": ")]
        public void TryParseReturnsNullForUnusableContent(string? content)
        {
            Assert.Null(Json.TryParse(content));
        }

        [Fact]
        public void TryParseSurvivesTheDocumentItParsedBeingDisposed()
        {
            // Json.TryParse clones the root element; without that the returned JsonElement would
            // point at freed memory and reading it would throw.
            JsonElement? parsed = Json.TryParse("{\"a\": 1}");
            Assert.Equal(1, Json.Property(parsed, "a")!.Value.GetInt32());
        }

        [Fact]
        public void PropertyReturnsNullForNonObjectsAndMissingKeys()
        {
            Assert.Null(Json.Property(null, "a"));
            Assert.Null(Json.Property(Json.TryParse("[1,2]"), "a"));
            Assert.Null(Json.Property(Json.TryParse("{\"a\":1}"), "b"));
        }

        [Fact]
        public void PropertyAsStringRendersNonStringScalars()
        {
            JsonElement? data = Json.TryParse(
                "{\"s\":\"x\",\"n\":42,\"b\":true,\"nil\":null,\"o\":{\"k\":1}}");

            Assert.Equal("x", Json.PropertyAsString(data, "s"));
            // A build id that arrives unquoted must still be usable, not dropped.
            Assert.Equal("42", Json.PropertyAsString(data, "n"));
            Assert.Equal("true", Json.PropertyAsString(data, "b"));
            Assert.Null(Json.PropertyAsString(data, "nil"));
            Assert.Null(Json.PropertyAsString(data, "missing"));
            Assert.Equal("{\"k\":1}", Json.PropertyAsString(data, "o"));
        }

        [Theory]
        [InlineData("{\"success\":true}", true)]
        [InlineData("{\"success\":\"true\"}", true)]
        [InlineData("{\"success\":\"TRUE\"}", true)]
        [InlineData("{\"success\":false}", false)]
        [InlineData("{\"success\":\"false\"}", false)]
        [InlineData("{\"success\":1}", false)]
        [InlineData("{}", false)]
        public void IsTrueAcceptsBothTheBooleanAndStringedSpellings(string body, bool expected)
        {
            Assert.Equal(expected, Json.IsTrue(Json.TryParse(body), "success"));
        }

        [Fact]
        public void IsTrueIsFalseForANullElement()
        {
            Assert.False(Json.IsTrue(null, "success"));
        }

        [Fact]
        public void ToObjectConvertsNestedStructuresToPlainClrTypes()
        {
            object? converted = Json.ToObject(Json.TryParse(
                "{\"n\":1,\"d\":1.5,\"s\":\"x\",\"t\":true,\"f\":false,\"nil\":null," +
                "\"arr\":[1,{\"k\":\"v\"}]}")!.Value);

            Dictionary<string, object?> dict = Assert.IsType<Dictionary<string, object?>>(converted);
            Assert.Equal(1L, dict["n"]);
            Assert.Equal(1.5, dict["d"]);
            Assert.Equal("x", dict["s"]);
            Assert.Equal(true, dict["t"]);
            Assert.Equal(false, dict["f"]);
            Assert.Null(dict["nil"]);

            List<object?> arr = Assert.IsType<List<object?>>(dict["arr"]);
            Assert.Equal(1L, arr[0]);
            Assert.Equal("v", Assert.IsType<Dictionary<string, object?>>(arr[1])["k"]);
        }

        [Fact]
        public void ToObjectHandlesABareScalarRoot()
        {
            Assert.Equal("hello", Json.ToObject(Json.TryParse("\"hello\"")!.Value));
            Assert.Null(Json.ToObject(Json.TryParse("null")!.Value));
        }
    }
}
