using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace MSBuildProjectTools.LanguageServer.Utilities
{
    /// <summary>
    ///     Helper methods for working with MSBuild projects.
    /// </summary>
    public static class MSBuildHelper
    {
        /// <summary>
        ///     The names of well-known item metadata.
        /// </summary>
        public static readonly ImmutableSortedSet<string> WellknownMetadataNames =
            ImmutableSortedSet.Create(
                "FullPath",
                "RootDir",
                "Filename",
                "Extension",
                "RelativeDir",
                "Directory",
                "RecursiveDir",
                "Identity",
                "ModifiedTime",
                "CreatedTime",
                "AccessedTime"
            );

        /// <summary>
        ///     The last .NET Core SDK version that had a "15.0" sub-folder for "Microsoft.Common.props" (later versions have a "Current" sub-folder instead).
        /// </summary>
        /// <remarks>
        ///     2.1.599 is the theoretical highest version number of the 2.1.5xx feature band, which is the last .NET Core SDK that ships .NET Core 2.1 (LTS).
        /// </remarks>
        static readonly SemanticVersion NetCoreLastSdkVersionFor150Folder = new SemanticVersion(major: 2, minor: 1, patch: 599);

        /// <summary>
        ///     Create an MSBuild project collection.
        /// </summary>
        /// <param name="solutionDirectory">
        ///     The base (i.e. solution) directory.
        /// </param>
        /// <param name="globalPropertyOverrides">
        ///     An optional dictionary containing property values to override.
        /// </param>
        /// <param name="logger">
        ///     An optional <see cref="ILogger"/> to use for diagnostic purposes (if not specified, the static <see cref="Log.Logger"/> will be used).
        /// </param>
        /// <returns>
        ///     The project collection.
        /// </returns>
        public static ProjectCollection CreateProjectCollection(string solutionDirectory, Dictionary<string, string> globalPropertyOverrides = null, ILogger logger = null)
        {
            if (logger == null)
                logger = Log.Logger;

            return CreateProjectCollection(solutionDirectory,
                DotNetRuntimeInfo.GetCurrent(solutionDirectory, logger),
                globalPropertyOverrides
            );
        }

        /// <summary>
        ///     Create an MSBuild project collection.
        /// </summary>
        /// <param name="solutionDirectory">
        ///     The base (i.e. solution) directory.
        /// </param>
        /// <param name="runtimeInfo">
        ///     Information about the current .NET Core runtime.
        /// </param>
        /// <param name="globalPropertyOverrides">
        ///     An optional dictionary containing property values to override.
        /// </param>
        /// <returns>
        ///     The project collection.
        /// </returns>
        public static ProjectCollection CreateProjectCollection(string solutionDirectory, DotNetRuntimeInfo runtimeInfo, Dictionary<string, string> globalPropertyOverrides = null)
        {
            if (String.IsNullOrWhiteSpace(solutionDirectory))
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'baseDir'.", nameof(solutionDirectory));

            if (runtimeInfo == null)
                throw new ArgumentNullException(nameof(runtimeInfo));

            if (String.IsNullOrWhiteSpace(runtimeInfo.BaseDirectory))
                throw new InvalidOperationException("Cannot determine base directory for .NET Core (check the output of 'dotnet --info').");

            Dictionary<string, string> globalProperties = CreateGlobalMSBuildProperties(runtimeInfo, solutionDirectory, globalPropertyOverrides);
            EnsureMSBuildEnvironment(globalProperties);

            ProjectCollection projectCollection = new ProjectCollection(globalProperties) { IsBuildEnabled = false };

            SemanticVersion netcoreVersion;
            if (!SemanticVersion.TryParse(runtimeInfo.Version, out netcoreVersion))
                throw new FormatException($"Cannot parse .NET Core version '{runtimeInfo.Version}' (does not appear to be a valid semantic version).");

            // Newer versions of the .NET Core SDK use the toolset version "Current" instead of "15.0" (tintoy/msbuild-project-tools-vscode#46).
            string toolsVersion = netcoreVersion <= NetCoreLastSdkVersionFor150Folder ? "15.0" : "Current";

            // Override toolset paths (for some reason these point to the main directory where the dotnet executable lives).
            Toolset toolset = projectCollection.GetToolset(toolsVersion);
            toolset = new Toolset(toolsVersion,
                toolsPath: runtimeInfo.BaseDirectory,
                projectCollection: projectCollection,
                msbuildOverrideTasksPath: ""
            );

            // Other toolset versions won't be supported by the .NET Core SDK
            projectCollection.RemoveAllToolsets();

            // TODO: Add configuration setting that enables user to configure custom toolsets.

            projectCollection.AddToolset(toolset);
            projectCollection.DefaultToolsVersion = toolsVersion;

            return projectCollection;
        }

        /// <summary>
        ///     Create global properties for MSBuild.
        /// </summary>
        /// <param name="runtimeInfo">
        ///     Information about the current .NET Core runtime.
        /// </param>
        /// <param name="solutionDirectory">
        ///     The base (i.e. solution) directory.
        /// </param>
        /// <param name="globalPropertyOverrides">
        ///     An optional dictionary containing property values to override.
        /// </param>
        /// <returns>
        ///     A dictionary containing the global properties.
        /// </returns>
        public static Dictionary<string, string> CreateGlobalMSBuildProperties(DotNetRuntimeInfo runtimeInfo, string solutionDirectory, Dictionary<string, string> globalPropertyOverrides = null)
        {
            if (runtimeInfo == null)
                throw new ArgumentNullException(nameof(runtimeInfo));

            if (String.IsNullOrWhiteSpace(solutionDirectory))
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'solutionDirectory'.", nameof(solutionDirectory));

            if (solutionDirectory.Length > 0 && solutionDirectory[solutionDirectory.Length - 1] != Path.DirectorySeparatorChar)
                solutionDirectory += Path.DirectorySeparatorChar;

            // Support overriding of SDKs path.
            string sdksPath = Environment.GetEnvironmentVariable("MSBuildSDKsPath");
            if (String.IsNullOrWhiteSpace(sdksPath))
                sdksPath = Path.Combine(runtimeInfo.BaseDirectory, "Sdks");

            var globalProperties = new Dictionary<string, string>
            {
                [WellKnownPropertyNames.DesignTimeBuild] = "true",
                [WellKnownPropertyNames.BuildProjectReferences] = "false",
                [WellKnownPropertyNames.ResolveReferenceDependencies] = "true",
                [WellKnownPropertyNames.SolutionDir] = solutionDirectory,
                [WellKnownPropertyNames.MSBuildExtensionsPath] = runtimeInfo.BaseDirectory,
                [WellKnownPropertyNames.MSBuildSDKsPath] = sdksPath,
                [WellKnownPropertyNames.RoslynTargetsPath] = Path.Combine(runtimeInfo.BaseDirectory, "Roslyn")
            };

            if (globalPropertyOverrides != null)
            {
                foreach (string propertyName in globalPropertyOverrides.Keys)
                    globalProperties[propertyName] = globalPropertyOverrides[propertyName];
            }

            return globalProperties;
        }

        /// <summary>
        ///     Ensure that environment variables are populated using the specified MSBuild global properties.
        /// </summary>
        /// <param name="globalMSBuildProperties">
        ///     The MSBuild global properties
        /// </param>
        public static void EnsureMSBuildEnvironment(Dictionary<string, string> globalMSBuildProperties)
        {
            if (globalMSBuildProperties == null)
                throw new ArgumentNullException(nameof(globalMSBuildProperties));

            // Kinda sucks that the simplest way to get MSBuild to resolve SDKs correctly is using environment variables, but there you go.
            Environment.SetEnvironmentVariable(
                WellKnownPropertyNames.MSBuildExtensionsPath,
                globalMSBuildProperties[WellKnownPropertyNames.MSBuildExtensionsPath]
            );
            Environment.SetEnvironmentVariable(
                WellKnownPropertyNames.MSBuildSDKsPath,
                globalMSBuildProperties[WellKnownPropertyNames.MSBuildSDKsPath]
            );
        }

        /// <summary>
        ///     Does the specified property name represent a private property?
        /// </summary>
        /// <param name="propertyName">
        ///     The property name.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the property name starts with an underscore; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsPrivateProperty(string propertyName) => propertyName?.StartsWith("_") ?? false;

        /// <summary>
        ///     Does the specified metadata name represent a private property?
        /// </summary>
        /// <param name="metadataName">
        ///     The metadata name.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the metadata name starts with an underscore; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsPrivateMetadata(string metadataName) => metadataName?.StartsWith("_") ?? false;

        /// <summary>
        ///     Does the specified item type represent a private property?
        /// </summary>
        /// <param name="itemType">
        ///     The item type.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the item type starts with an underscore; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsPrivateItemType(string itemType) => itemType?.StartsWith("_") ?? false;

        /// <summary>
        ///     Determine whether the specified metadata name represents well-known (built-in) item metadata.
        /// </summary>
        /// <param name="metadataName">
        ///     The metadata name.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if <paramref name="metadataName"/> represents well-known item metadata; otherwise, <c>false</c>.
        /// </returns>
        public static bool IsWellKnownItemMetadata(string metadataName) => WellknownMetadataNames.Contains(metadataName);

        /// <summary>
        ///     Create a copy of the project for caching.
        /// </summary>
        /// <param name="project">
        ///     The MSBuild project.
        /// </param>
        /// <returns>
        ///     The project copy (independent of original, but sharing the same <see cref="ProjectCollection"/>).
        /// </returns>
        /// <remarks>
        ///     You can only create a single cached copy for a given project.
        /// </remarks>
        public static Project CloneAsCachedProject(this Project project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            ProjectRootElement clonedXml = project.Xml.DeepClone();
            Project clonedProject = new Project(clonedXml, project.GlobalProperties, project.ToolsVersion, project.ProjectCollection);
            clonedProject.FullPath = Path.ChangeExtension(project.FullPath,
                ".cached" + Path.GetExtension(project.FullPath)
            );

            return clonedProject;
        }

        /// <summary>
        ///     Get the project assets file (usually "project.assets.json").
        /// </summary>
        /// <param name="project">
        ///     The MSBuild <see cref="Project"/>.
        /// </param>
        /// <returns>
        ///     A <see cref="FileInfo"/> representing the project assets file, or <c>null</c> if the project does not have a <see cref="WellKnownPropertyNames.ProjectAssetsFile"/> property.
        /// </returns>
        public static FileInfo GetProjectAssetsFile(this Project project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            string projectAssetsFile = project.GetPropertyValue(WellKnownPropertyNames.ProjectAssetsFile);
            if (String.IsNullOrWhiteSpace(projectAssetsFile))
                return null;

            if (!Path.IsPathRooted(projectAssetsFile))
                projectAssetsFile = Path.Combine(project.DirectoryPath, projectAssetsFile);

            return new FileInfo(projectAssetsFile);
        }

        /// <summary>
        ///     Asynchronously scan the project's assets file ("project.asset.json") to determine the actual versions of all NuGet packages referenced by the project.
        /// </summary>
        /// <param name="project">
        ///     The MSBuild <see cref="Project"/>.
        /// </param>
        /// <param name="cancellationToken">
        ///     An optional <see cref="CancellationToken"/> that can be used to cancel the asynchronous operation.
        /// </param>
        /// <returns>
        ///     A dictionary of package semantic versions (keyed by package Id), or <c>null</c> if the project does not have a <see cref="WellKnownPropertyNames.ProjectAssetsFile"/> property (or the project assets file does not exist or has an invalid format).
        /// </returns>
        public static async Task<Dictionary<string, SemanticVersion>> GetReferencedPackageVersions(this Project project, CancellationToken cancellationToken = default)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            FileInfo projectAssetsFile = project.GetProjectAssetsFile();
            if (projectAssetsFile == null)
                return null;

            if (!projectAssetsFile.Exists)
                return null;

            JObject projectAssetsJson;
            try
            {
                using (TextReader reader = projectAssetsFile.OpenText())
                using (JsonReader jsonReader = new JsonTextReader(reader))
                {
                    projectAssetsJson = await JObject.LoadAsync(jsonReader, cancellationToken);
                }
            }
            catch (Exception cannotLoadProjectAssetsJson)
            {
                Log.Error(cannotLoadProjectAssetsJson, "Unable to load project assets file {ProjectAssetsFile}.", projectAssetsFile.FullName);

                return null;
            }

            JObject libraries = projectAssetsJson.Value<JObject>("libraries");
            if (libraries == null)
            {
                Log.Warning("Project assets file {ProjectAssetsFile} has invalid format (missing 'libraries' property on root object).", projectAssetsFile.FullName);

                return null;
            }

            var referencedPackageVersions = new Dictionary<string, SemanticVersion>(StringComparer.OrdinalIgnoreCase);

            foreach (JProperty library in libraries.Properties())
            {
                // Property names should be in the format "libName/semVer".
                string[] nameComponents = library.Name.Split(
                    separator: new[] {'/'},
                    count: 2
                );
                if (nameComponents.Length != 2)
                    continue; // Invalid format.

                string name = nameComponents[0];
                SemanticVersion version;
                if (!SemanticVersion.TryParse(nameComponents[1], out version))
                    continue; // Not a valid semantic version.

                referencedPackageVersions[name] = version;
            }

            return referencedPackageVersions;
        }

        /// <summary>
        ///     The names of well-known MSBuild properties.
        /// </summary>
        public static class WellKnownPropertyNames
        {
            /// <summary>
            ///     The "MSBuildExtensionsPath" property.
            /// </summary>
            public static readonly string MSBuildExtensionsPath = "MSBuildExtensionsPath";

            /// <summary>
            ///     The "MSBuildExtensionsPath32" property.
            /// </summary>
            public static readonly string MSBuildExtensionsPath32 = "MSBuildExtensionsPath32";

            /// <summary>
            ///     The "MSBuildSDKsPath" property.
            /// </summary>
            public static readonly string MSBuildSDKsPath = "MSBuildSDKsPath";

            /// <summary>
            ///     The "MSBuildToolsPath" property.
            /// </summary>
            public static readonly string MSBuildToolsPath = "MSBuildToolsPath";

            /// <summary>
            ///     The "SolutionDir" property.
            /// </summary>
            public static readonly string SolutionDir = "SolutionDir";

            /// <summary>
            ///     The "_ResolveReferenceDependencies" property.
            /// </summary>
            public static readonly string ResolveReferenceDependencies = "_ResolveReferenceDependencies";

            /// <summary>
            ///     The "DesignTimeBuild" property.
            /// </summary>
            public static readonly string DesignTimeBuild = "DesignTimeBuild";

            /// <summary>
            ///     The "BuildProjectReferences" property.
            /// </summary>
            public static readonly string BuildProjectReferences = "BuildProjectReferences";

            /// <summary>
            ///     The "RoslynTargetsPath" property.
            /// </summary>
            public static readonly string RoslynTargetsPath = "RoslynTargetsPath";

            /// <summary>
            ///     The "ProjectAssetsFile" property.
            /// </summary>
            public static readonly string ProjectAssetsFile = "ProjectAssetsFile";
        }
    }
}
