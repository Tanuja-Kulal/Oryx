// --------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// --------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Oryx.BuildScriptGenerator.DotNetCore
{
    /// <summary>
    /// .NET Core platform.
    /// </summary>
    [BuildProperty(
        DotNetCoreConstants.ProjectBuildPropertyKey,
        DotNetCoreConstants.ProjectBuildPropertyKeyDocumentation)]
    internal class DotNetCorePlatform : IProgrammingPlatform
    {
        private readonly IDotNetCoreVersionProvider _versionProvider;
        private readonly DefaultProjectFileProvider _projectFileProvider;
        private readonly ILogger<DotNetCorePlatform> _logger;
        private readonly DotNetCorePlatformDetector _detector;
        private readonly DotNetCoreScriptGeneratorOptions _dotNetCoreScriptGeneratorOptions;
        private readonly BuildScriptGeneratorOptions _cliOptions;
        private readonly IEnvironment _environment;
        private readonly DotNetCorePlatformInstaller _platformInstaller;
        private readonly GlobalJsonSdkResolver _globalJsonSdkResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="DotNetCorePlatform"/> class.
        /// </summary>
        /// <param name="versionProvider">The .NET version provider.</param>
        /// <param name="projectFileProvider">The project file provider.</param>
        /// <param name="environmentSettingsProvider">The environment settings provider.</param>
        /// <param name="logger">The logger of .NET platform.</param>
        /// <param name="detector">The detector of .NET platform.</param>
        /// <param name="cliOptions">The build options for BuildScriptGenerator.</param>
        /// <param name="dotNetCoreScriptGeneratorOptions">The options if .NET platform.</param>
        /// <param name="platformInstaller">The <see cref="DotNetCorePlatformInstaller"/>.</param>
        public DotNetCorePlatform(
            IDotNetCoreVersionProvider versionProvider,
            DefaultProjectFileProvider projectFileProvider,
            ILogger<DotNetCorePlatform> logger,
            DotNetCorePlatformDetector detector,
            IOptions<BuildScriptGeneratorOptions> cliOptions,
            IOptions<DotNetCoreScriptGeneratorOptions> dotNetCoreScriptGeneratorOptions,
            IEnvironment environment,
            DotNetCorePlatformInstaller platformInstaller,
            GlobalJsonSdkResolver globalJsonSdkResolver)
        {
            _versionProvider = versionProvider;
            _projectFileProvider = projectFileProvider;
            _logger = logger;
            _detector = detector;
            _dotNetCoreScriptGeneratorOptions = dotNetCoreScriptGeneratorOptions.Value;
            _cliOptions = cliOptions.Value;
            _environment = environment;
            _platformInstaller = platformInstaller;
            _globalJsonSdkResolver = globalJsonSdkResolver;
        }

        /// <inheritdoc/>
        public string Name => DotNetCoreConstants.PlatformName;

        /// <inheritdoc/>
        public IEnumerable<string> SupportedVersions
        {
            get
            {
                var versionMap = _versionProvider.GetSupportedVersions();

                // Map is from runtime version => sdk version
                return versionMap.Keys;
            }
        }

        /// <inheritdoc/>
        public PlatformDetectorResult Detect(RepositoryContext context)
        {
            return _detector.Detect(context);
        }

        /// <inheritdoc/>
        public BuildScriptSnippet GenerateBashBuildScriptSnippet(BuildScriptGeneratorContext context)
        {
            var versionMap = _versionProvider.GetSupportedVersions();

            string installationScriptSnippet = null;
            string globalJsonSdkVersion = null;
            if (_cliOptions.EnableDynamicInstall)
            {
                _logger.LogDebug("Dynamic install is enabled.");

                var availableSdks = versionMap.Values;
                globalJsonSdkVersion = _globalJsonSdkResolver.GetSatisfyingSdkVersion(
                    context.SourceRepo,
                    context.ResolvedDotNetCoreRuntimeVersion,
                    availableSdks);

                if (_platformInstaller.IsVersionAlreadyInstalled(
                    context.ResolvedDotNetCoreRuntimeVersion,
                    globalJsonSdkVersion))
                {
                    _logger.LogDebug(
                        "DotNetCore runtime version {runtimeVersion} is already installed. " +
                        "So skipping installing it again.",
                        context.ResolvedDotNetCoreRuntimeVersion);
                }
                else
                {
                    _logger.LogDebug(
                        "DotNetCore runtime version {runtimeVersion} is not installed. " +
                        "So generating an installation script snippet for it.",
                        context.ResolvedDotNetCoreRuntimeVersion);

                    installationScriptSnippet = _platformInstaller.GetInstallerScriptSnippet(
                        context.ResolvedDotNetCoreRuntimeVersion,
                        globalJsonSdkVersion);
                }
            }
            else
            {
                _logger.LogDebug("Dynamic install is not enabled.");
            }

            var manifestFileProperties = new Dictionary<string, string>();
            manifestFileProperties[ManifestFilePropertyKeys.OperationId] = context.OperationId;
            manifestFileProperties[ManifestFilePropertyKeys.DotNetCoreRuntimeVersion]
                = context.ResolvedDotNetCoreRuntimeVersion;

            if (string.IsNullOrEmpty(globalJsonSdkVersion))
            {
                manifestFileProperties[ManifestFilePropertyKeys.DotNetCoreSdkVersion]
                    = versionMap[context.ResolvedDotNetCoreRuntimeVersion];
            }
            else
            {
                manifestFileProperties[ManifestFilePropertyKeys.DotNetCoreSdkVersion] = globalJsonSdkVersion;
            }


            var projectFile = _projectFileProvider.GetRelativePathToProjectFile(context);
            if (string.IsNullOrEmpty(projectFile))
            {
                return null;
            }

            (var preBuildCommand, var postBuildCommand) = PreAndPostBuildCommandHelper.GetPreAndPostBuildCommands(
                context.SourceRepo,
                _cliOptions);

            var sourceDir = _cliOptions.SourceDir;
            var temporaryDestinationDir = "/tmp/puboutput";
            var destinationDir = _cliOptions.DestinationDir;
            var intermediateDir = _cliOptions.IntermediateDir;
            var hasUserSuppliedDestinationDir = !string.IsNullOrEmpty(_cliOptions.DestinationDir);
            var buildConfiguration = GetBuildConfiguration();

            // Since destination directory is optional for .NET Core builds, check
            var outputIsSubDirOfSourceDir = false;
            if (!string.IsNullOrEmpty(_cliOptions.DestinationDir))
            {
                outputIsSubDirOfSourceDir = DirectoryHelper.IsSubDirectory(
                    _cliOptions.DestinationDir,
                    _cliOptions.SourceDir);
            }

            var scriptBuilder = new StringBuilder();
            scriptBuilder
                .AppendLine("#!/bin/bash")
                .AppendLine("set -e")
                .AppendLine();

            // For 1st build this is not a problem, but for subsequent builds we want the source directory to be
            // in a clean state to avoid considering earlier build's state and potentially yielding incorrect results.
            if (outputIsSubDirOfSourceDir)
            {
                scriptBuilder.AppendLine($"rm -rf {_cliOptions.DestinationDir}");
            }

            scriptBuilder.AddScriptToCopyToIntermediateDirectory(
                    sourceDir: ref sourceDir,
                    intermediateDir: intermediateDir,
                    GetDirectoriesToExcludeFromCopyToIntermediateDir(context))
                .AppendFormatWithLine("cd \"{0}\"", sourceDir)
                .AppendLine();

            if (!string.IsNullOrEmpty(installationScriptSnippet))
            {
                scriptBuilder.AppendLine(installationScriptSnippet);
            }

            scriptBuilder.AddScriptToCopyToIntermediateDirectory(
                sourceDir: ref sourceDir,
                intermediateDir: intermediateDir,
                GetDirectoriesToExcludeFromCopyToIntermediateDir(context))
            .AppendFormatWithLine("cd \"{0}\"", sourceDir)
            .AppendLine();

            scriptBuilder
                .AddScriptToSetupSourceAndDestinationDirectories(
                    sourceDir: sourceDir,
                    temporaryDestinationDir: temporaryDestinationDir,
                    destinationDir: destinationDir,
                    hasUserSuppliedDestinationDir: hasUserSuppliedDestinationDir)
                .AppendBenvCommand($"dotnet={context.ResolvedDotNetCoreRuntimeVersion}")
                .AddScriptToRunPreBuildCommand(sourceDir: sourceDir, preBuildCommand: preBuildCommand)
                .AppendLine("echo")
                .AppendLine("dotnetCoreVersion=$(dotnet --version)")
                .AppendLine("echo \"Using .NET Core SDK Version: $dotnetCoreVersion\"")
                .AppendLine()
                .AddScriptToRestorePackages(projectFile);

            if (hasUserSuppliedDestinationDir)
            {
                scriptBuilder
                    .AddScriptToPublishOutput(
                        projectFile: projectFile,
                        buildConfiguration: buildConfiguration,
                        finalDestinationDir: destinationDir)
                    .AddScriptToRunPostBuildCommand(
                        sourceDir: sourceDir,
                        postBuildCommand: postBuildCommand);
            }
            else
            {
                scriptBuilder
                    .AddScriptToBuildProject(projectFile)
                    .AddScriptToRunPostBuildCommand(
                        sourceDir: sourceDir,
                        postBuildCommand: postBuildCommand);
            }

            SetStartupFileNameInfoInManifestFile(context, projectFile, manifestFileProperties);

            scriptBuilder
                .AddScriptToCreateManifestFile(
                    manifestFileProperties,
                    manifestDir: _cliOptions.ManifestDir,
                    finalDestinationDir: destinationDir)
                .AppendLine("echo Done.");

            return new BuildScriptSnippet
            {
                BashBuildScriptSnippet = scriptBuilder.ToString(),
                IsFullScript = true,
            };
        }

        /// <inheritdoc/>
        public bool IsCleanRepo(ISourceRepo repo)
        {
            return true;
        }

        /// <inheritdoc/>
        public string GenerateBashRunTimeInstallationScript(RunTimeInstallationScriptGeneratorOptions options)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public bool IsEnabled(RepositoryContext ctx)
        {
            return _cliOptions.EnableDotNetCoreBuild;
        }

        /// <inheritdoc/>
        public bool IsEnabledForMultiPlatformBuild(RepositoryContext ctx)
        {
            // A user has the power to either enable or disable multi-platform builds entirely.
            // However if user enables it, ASP.NET Core platform still explicitly opts out of it.
            return false;
        }

        /// <inheritdoc/>
        public void SetRequiredTools(
            ISourceRepo sourceRepo,
            string targetPlatformVersion,
            IDictionary<string, string> toolsToVersion)
        {
            Debug.Assert(toolsToVersion != null, $"{nameof(toolsToVersion)} must not be null.");
            if (!string.IsNullOrWhiteSpace(targetPlatformVersion))
            {
                toolsToVersion[ToolNameConstants.DotNetName] = targetPlatformVersion;
            }
        }

        /// <inheritdoc/>
        public void SetVersion(BuildScriptGeneratorContext context, string runtimeVersion)
        {
            context.ResolvedDotNetCoreRuntimeVersion = runtimeVersion;
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetDirectoriesToExcludeFromCopyToBuildOutputDir(
            BuildScriptGeneratorContext scriptGeneratorContext)
        {
            var dirs = new List<string>();
            dirs.Add("obj");
            dirs.Add("bin");
            return dirs;
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetDirectoriesToExcludeFromCopyToIntermediateDir(
            BuildScriptGeneratorContext scriptGeneratorContext)
        {
            var dirs = new List<string>();
            dirs.Add(".git");
            dirs.Add("obj");
            dirs.Add("bin");
            return dirs;
        }

        private string GetBuildConfiguration()
        {
            var configuration = _dotNetCoreScriptGeneratorOptions.MSBuildConfiguration;
            if (string.IsNullOrEmpty(configuration))
            {
                configuration = DotNetCoreConstants.DefaultMSBuildConfiguration;
            }

            return configuration;
        }

        /// <summary>
        /// Even though the runtime container has the logic of finding out the startup file based on
        /// 'runtimeconfig.json' prefix, we still set the name in the manifest file because of the following
        /// scenario: let's say output directory currently has 'foo.dll' and user made a change to the project
        /// name or assembly name property to 'bar' which causes 'bar.dll' to be published. If the output
        /// directory was NOT cleaned, then we would now be having both 'foo.runtimeconfig.json' and
        /// 'bar.runtimeconfig.json' which causes a problem for runtime container as it cannot figure out the
        /// right startup DLL. So, to help that scenario we always set the start-up file name in manifest file.
        /// The runtime container will first look into manifest file to find the startup filename, if the
        /// file name is not present or if a manifest file is not present at all(ex: in case of VS Publish where
        /// the build does not happen with Oryx), then the runtime container's logic will fallback to looking at
        /// runtimeconfig.json prefixes.
        /// </summary>
        private void SetStartupFileNameInfoInManifestFile(
            BuildScriptGeneratorContext context,
            string projectFile,
            IDictionary<string, string> buildProperties)
        {
            string startupDllFileName;
            var projectFileContent = context.SourceRepo.ReadFile(projectFile);
            var projFileDoc = XDocument.Load(new StringReader(projectFileContent));
            var assemblyNameElement = projFileDoc.XPathSelectElement(DotNetCoreConstants.AssemblyNameXPathExpression);
            if (assemblyNameElement == null)
            {
                var name = Path.GetFileNameWithoutExtension(projectFile);
                startupDllFileName = $"{name}.dll";
            }
            else
            {
                startupDllFileName = $"{assemblyNameElement.Value}.dll";
            }

            buildProperties[DotNetCoreManifestFilePropertyKeys.StartupDllFileName] = startupDllFileName;
        }
    }
}