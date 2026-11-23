using WinUiXaml.Xaml;

namespace WinUiXaml.Xaml.Tests
{
    /// <summary>
    /// Implicit namespaces (Simpler XAML) are opt-in: only a caller that knows its project uses the LibreWPF XAML source generator
    /// asks for them. Everything else reads a document as written, so a missing xmlns stays visible.
    /// </summary>
    public class ImplicitNamespaceTests
    {
        private const string PresentationUri = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private const string XamlUri = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string NewStyle = "<Window x:Class=\"App.MainWindow\"><Grid><Button x:Name=\"Go\" /></Grid></Window>";

        [Fact]
        public void ByDefault_ADocumentWithoutXmlns_ResolvesNothing()
        {
            var root = XamlParser.Parse(NewStyle).Root!;

            Assert.False(root.NamespaceScope.TryResolvePrefix(null, out _));
            Assert.False(root.NamespaceScope.TryResolvePrefix("x", out _));
        }

        [Fact]
        public void WithImplicitNamespaces_TheRootAndItsChildrenResolveThem()
        {
            var root = XamlParser.Parse(NewStyle, implicitNamespaces: true).Root!;

            Assert.True(root.NamespaceScope.TryResolvePrefix(null, out var presentation));
            Assert.Equal(PresentationUri, presentation);
            Assert.True(root.NamespaceScope.TryResolvePrefix("x", out var x));
            Assert.Equal(XamlUri, x);
            var button = TreeQuery.Elements(XamlParser.Parse(NewStyle, implicitNamespaces: true)).First(e => e.Name?.LocalName == "Button");
            Assert.True(button.NamespaceScope.TryResolvePrefix(null, out _));
        }

        [Fact]
        public void ADocumentThatDeclaresItsNamespaces_IsTheSameWithOrWithoutTheFlag()
        {
            var text = "<Page xmlns=\"" + PresentationUri + "\" xmlns:x=\"" + XamlUri + "\" xmlns:local=\"using:App\"><Grid /></Page>";

            foreach (var flag in new[] { false, true })
            {
                var scope = XamlParser.Parse(text, flag).Root!.NamespaceScope;
                Assert.True(scope.TryResolvePrefix("local", out var local));
                Assert.Equal("using:App", local);
                Assert.True(scope.TryResolvePrefix(null, out var presentation));
                Assert.Equal(PresentationUri, presentation);
            }
        }

        [Fact]
        public void ARemappedDefaultNamespace_IsNotReplacedByTheImplicitOne()
        {
            var root = XamlParser.Parse("<Page xmlns=\"using:App\"><Grid /></Page>", implicitNamespaces: true).Root!;

            Assert.True(root.NamespaceScope.TryResolvePrefix(null, out var def));
            Assert.Equal("using:App", def);
        }

        [Fact]
        public void TheClassOfANewStyleDocument_IsFoundEvenThoughItsDiagnosticsStayStrict()
        {
            Assert.Equal("App.MainWindow", XamlIntrospection.GetClass(NewStyle));
            Assert.False(XamlParser.Parse(NewStyle).Root!.NamespaceScope.TryResolvePrefix("x", out _));
        }

        [Fact]
        public void TheClassOfADocumentWithoutOne_IsNull()
        {
            Assert.Null(XamlIntrospection.GetClass("<Window><Grid /></Window>"));
        }

        [Fact]
        public void TheOneArgumentOverload_IsTheStrictReading()
        {
            Assert.False(XamlParser.Parse(NewStyle).Root!.NamespaceScope.TryResolvePrefix("x", out _));
        }
    }
}
