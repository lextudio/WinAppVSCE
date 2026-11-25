using WinUiXaml.Xaml;
using Xunit;

namespace WinUiXaml.Xaml.Tests
{
    public class ExpressionDecodeTests
    {
        [Theory]
        [InlineData("a && b", "a && b")]
        [InlineData("a & b", "a & b")]
        [InlineData("trailing &", "trailing &")]
        [InlineData("x &am", "x &am")]
        [InlineData("a &amp;&amp; b", "a && b")]
        [InlineData("a &lt; b &gt; c", "a < b > c")]
        public void Decode_NeverThrows_OnAnUnescapedOrTruncatedAmpersand_AndMapsEntities(string raw, string expected)
        {
            var decoded = XamlExpressionSyntax.Decode(raw, 0, raw.Length, out var offsets);
            Assert.Equal(expected, decoded);
            Assert.True(offsets.Length >= expected.Length + 1);
        }

        [Fact]
        public void Decode_OfASlice_DoesNotReadPastItsEnd()
        {
            // The slice ends in the middle of an entity: "&amp" (no semicolon in the slice) is not an entity.
            const string raw = "{x &amp; y}";
            var decoded = XamlExpressionSyntax.Decode(raw, 1, 6, out _);
            Assert.Equal("x &amp", decoded);
        }
    }
}
