using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace WinUiXaml.Workspace
{
    /// <summary>Fast first-stage reference resolver for <c>ResolveFrameworkAsync</c>, using out-of-process MSBuild data without parsing sources or running generators.</summary>
    internal sealed class MsBuildFrameworkProject
    {
        private MsBuildFrameworkProject(
            Compilation compilation,
            ImmutableArray<string> xamlFiles,
            string? applicationDefinitionPath,
            ImmutableArray<string> unresolvedProjectReferences,
            bool usesMinimalMetadataFallback)
        {
            Compilation = compilation;
            XamlFiles = xamlFiles;
            ApplicationDefinitionPath = applicationDefinitionPath;
            UnresolvedProjectReferences = unresolvedProjectReferences;
            UsesMinimalMetadataFallback = usesMinimalMetadataFallback;
        }

        internal Compilation Compilation { get; }
        internal ImmutableArray<string> XamlFiles { get; }
        internal string? ApplicationDefinitionPath { get; }

        /// <summary>Referenced projects that had never been built when this project loaded.</summary>
        internal ImmutableArray<string> UnresolvedProjectReferences { get; }

        /// <summary>Whether this resolution contains the profile's intentionally synthetic design-host metadata.</summary>
        internal bool UsesMinimalMetadataFallback { get; }

        internal static MsBuildFrameworkProject? Load(
            string projectPath,
            XamlFrameworkProfile profile,
            IReadOnlyDictionary<string, string> globalProperties,
            CancellationToken cancellationToken = default)
        {
            var properties = globalProperties.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

            var fullPath = Path.GetFullPath(projectPath);
            var (evaluation, arguments) = MsBuildCli.ResolveFrameworkReferences(
                fullPath,
                properties,
                cancellationToken);
            if (RoslynProjectWorkspace.RequiresRestore(
                evaluation.ProjectAssetsFile,
                evaluation.HasPackageReferences))
            {
                throw new ProjectRestoreRequiredException(projectPath);
            }

            if (arguments.IsDefaultOrEmpty)
            {
                return null;
            }

            var commandLine = CscCommandLine.Parse(arguments, Path.GetDirectoryName(fullPath)!);
            // No documentation at this stage: the base branch omitted it here, and adding it makes
            // completion prose appear as soon as a file opens, visibly changing editor behavior.
            var references = commandLine.CreateMetadataReferences(includeDocumentation: false);
            if (references.IsDefaultOrEmpty)
            {
                return null;
            }

            var compilation = CSharpCompilation.Create(
                commandLine.AssemblyName
                    ?? evaluation.AssemblyName
                    ?? "WinUiXaml.Framework",
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Design-host fixtures intentionally omit a framework closure. They get only the
            // small profile-specific surface below; ordinary projects must still supply real
            // metadata or this stage declines to answer.
            var usesMinimalMetadataFallback = compilation.GetTypeByMetadataName(profile.SentinelMetadataName) is null &&
                profile.AllowsMinimalMetadataFallback &&
                ((profile.Id == "winui" && evaluation.PackageReferences.IsDefaultOrEmpty) ||
                 evaluation.IsProGpuWinUiProject ||
                 evaluation.AllowsMinimalMetadataFallback);
            if (usesMinimalMetadataFallback)
            {
                compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(profile.Id is "wpf" or "librewpf" ? """
                    namespace System.Windows
                    {
                        public class DependencyObject { }
                        public class UIElement : DependencyObject { }
                        public class FrameworkElement : UIElement { }
                        public class Window : FrameworkElement
                        {
                            public UIElement? Content { get; set; }
                        }
                    }
                    namespace System.Windows.Controls
                    {
                        public class Grid : System.Windows.FrameworkElement { }
                        public class StackPanel : System.Windows.FrameworkElement { }
                        public class TextBlock : System.Windows.FrameworkElement { }
                        public class Button : System.Windows.FrameworkElement { }
                    }
                    """ : """
                    namespace Microsoft.UI.Xaml
                    {
                        public class DependencyObject { }
                        public class UIElement : DependencyObject { }
                        public class FrameworkElement : UIElement { }
                        public class FrameworkTemplate { }
                    }
                    namespace Microsoft.UI.Xaml.Controls
                    {
                        public class Page : Microsoft.UI.Xaml.FrameworkElement
                        {
                            public Microsoft.UI.Xaml.UIElement? Content { get; set; }
                        }
                        public class Grid : Microsoft.UI.Xaml.FrameworkElement { }
                        public class StackPanel : Microsoft.UI.Xaml.FrameworkElement { }
                        public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { }
                        public class Button : Microsoft.UI.Xaml.FrameworkElement { }
                    }
                    """));
            }

            if (compilation.GetTypeByMetadataName(profile.SentinelMetadataName) is null)
            {
                return null;
            }

            return new MsBuildFrameworkProject(
                compilation,
                evaluation.XamlFiles,
                evaluation.ApplicationDefinition,
                // This stage does not build project references, so only the full stage can report
                // build-required from what it actually observed.
                ImmutableArray<string>.Empty,
                usesMinimalMetadataFallback);
        }

        internal static MsBuildFrameworkProject? Load(
            string projectPath,
            IReadOnlyDictionary<string, string> globalProperties,
            CancellationToken cancellationToken = default) =>
            Load(projectPath, XamlFrameworkProfile.WinUi, globalProperties, cancellationToken);
    }
}
