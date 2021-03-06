﻿// --------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// --------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Oryx.BuildScriptGenerator.SourceRepo;
using Microsoft.Oryx.Common.Extensions;
using Newtonsoft.Json.Linq;

namespace Microsoft.Oryx.BuildScriptGenerator.Node
{
    /// <summary>
    /// Node.js Platform.
    /// </summary>
    [BuildProperty(RegistryUrlPropertyKey, "Custom npm registry URL. Will be written to .npmrc during the build.")]
    [BuildProperty(
        CompressNodeModulesPropertyKey,
        "Indicates how and if 'node_modules' folder should be compressed into a single file in the output folder. " +
        "Options are '" + ZipNodeModulesOption + "', and '" + TarGzNodeModulesOption + "'. Default is to not compress. " +
        "If this option is used, when running the app the node_modules folder must be extracted from this file.")]
    [BuildProperty(
        PruneDevDependenciesPropertyKey,
        "When using different intermediate and output folders, only the prod dependencies are copied to the output. " +
        "Options are 'true', blank (same meaning as 'true'), and 'false'. Default is false.")]
    [BuildProperty(
        RequireBuildPropertyKey,
        "Requires either 'npm run build' or 'yarn run build' or custom run build command  to be run. Default is false. " +
        "If value is not provided, it is assumed to be 'true'.")]
    internal class NodePlatform : IProgrammingPlatform
    {
        /// <summary>
        /// Property key of Registry URL.
        /// </summary>
        internal const string RegistryUrlPropertyKey = "npm_registry_url";

        /// <summary>
        /// Property key of compress_node_modules.
        /// </summary>
        internal const string CompressNodeModulesPropertyKey = "compress_node_modules";

        /// <summary>
        /// Property key of prune_dev_dependencies.
        /// </summary>
        internal const string PruneDevDependenciesPropertyKey = "prune_dev_dependencies";

        /// <summary>
        /// Property key of require_build.
        /// </summary>
        internal const string RequireBuildPropertyKey = "require_build";

        /// <summary>
        /// The zip option for node modules.
        /// </summary>
        internal const string ZipNodeModulesOption = "zip";

        /// <summary>
        /// The tar-gz option for node modules.
        /// </summary>
        internal const string TarGzNodeModulesOption = "tar-gz";
        private readonly BuildScriptGeneratorOptions _commonOptions;
        private readonly NodeScriptGeneratorOptions _nodeScriptGeneratorOptions;
        private readonly INodeVersionProvider _nodeVersionProvider;
        private readonly ILogger<NodePlatform> _logger;
        private readonly NodePlatformDetector _detector;
        private readonly IEnvironment _environment;
        private readonly NodePlatformInstaller _platformInstaller;

        /// <summary>
        /// Initializes a new instance of the <see cref="NodePlatform"/> class.
        /// </summary>
        /// <param name="nodeScriptGeneratorOptions">The options for nodeScriptGenerator.</param>
        /// <param name="nodeVersionProvider">The Node.js version provider.</param>
        /// <param name="logger">The logger of Node.js platform.</param>
        /// <param name="detector">The detector of Node.js platform.</param>
        /// <param name="environment">The environment of Node.js platform.</param>
        public NodePlatform(
            IOptions<BuildScriptGeneratorOptions> commonOptions,
            IOptions<NodeScriptGeneratorOptions> nodeScriptGeneratorOptions,
            INodeVersionProvider nodeVersionProvider,
            ILogger<NodePlatform> logger,
            NodePlatformDetector detector,
            IEnvironment environment,
            NodePlatformInstaller nodePlatformInstaller)
        {
            _commonOptions = commonOptions.Value;
            _nodeScriptGeneratorOptions = nodeScriptGeneratorOptions.Value;
            _nodeVersionProvider = nodeVersionProvider;
            _logger = logger;
            _detector = detector;
            _environment = environment;
            _platformInstaller = nodePlatformInstaller;
        }

        /// <inheritdoc/>
        public string Name => NodeConstants.PlatformName;

        /// <inheritdoc/>
        public IEnumerable<string> SupportedVersions
        {
            get
            {
                var versionInfo = _nodeVersionProvider.GetVersionInfo();
                return versionInfo.SupportedVersions;
            }
        }

        /// <inheritdoc/>
        public PlatformDetectorResult Detect(RepositoryContext context)
        {
            return _detector.Detect(context);
        }

        /// <inheritdoc/>
        public BuildScriptSnippet GenerateBashBuildScriptSnippet(BuildScriptGeneratorContext ctx)
        {
            string installationScriptSnippet = null;
            if (_commonOptions.EnableDynamicInstall)
            {
                _logger.LogDebug("Dynamic install is enabled.");

                if (_platformInstaller.IsVersionAlreadyInstalled(ctx.ResolvedNodeVersion))
                {
                    _logger.LogDebug(
                        "Node version {version} is already installed. So skipping installing it again.",
                        ctx.ResolvedNodeVersion);
                }
                else
                {
                    _logger.LogDebug(
                        "Node version {version} is not installed. So generating an installation script snippet for it.",
                        ctx.ResolvedNodeVersion);

                    installationScriptSnippet = _platformInstaller.GetInstallerScriptSnippet(ctx.ResolvedNodeVersion);
                }
            }
            else
            {
                _logger.LogDebug("Dynamic install not enabled.");
            }

            var manifestFileProperties = new Dictionary<string, string>();

            // Write the version to the manifest file
            manifestFileProperties[ManifestFilePropertyKeys.NodeVersion] = ctx.ResolvedNodeVersion;

            var packageJson = GetPackageJsonObject(ctx.SourceRepo, _logger);
            string runBuildCommand = null;
            string runBuildAzureCommand = null;
            bool configureYarnCache = false;
            string packageManagerCmd = null;
            string packageInstallCommand = null;
            string packageInstallerVersionCommand = null;

            if (ctx.SourceRepo.FileExists(NodeConstants.YarnLockFileName))
            {
                packageManagerCmd = NodeConstants.YarnCommand;
                packageInstallCommand = NodeConstants.YarnPackageInstallCommand;
                configureYarnCache = true;
                packageInstallerVersionCommand = NodeConstants.YarnVersionCommand;
            }
            else if (StaticSiteGeneratorHelper.IsHugoApp(ctx.SourceRepo, _environment))
            {
                packageManagerCmd = NodeConstants.HugoCommand;
                packageInstallCommand = NodeConstants.HugoCommand;
                packageInstallerVersionCommand = NodeConstants.HugoVersionCommand;
            }
            else
            {
                packageManagerCmd = NodeConstants.NpmCommand;
                packageInstallCommand = NodeConstants.NpmPackageInstallCommand;
                packageInstallerVersionCommand = NodeConstants.NpmVersionCommand;
            }

            _logger.LogInformation("Using {packageManager}", packageManagerCmd);

            var hasProdDependencies = false;
            if (packageJson?.dependencies != null)
            {
                hasProdDependencies = true;
            }

            var hasDevDependencies = false;
            if (packageJson?.devDependencies != null)
            {
                // If development time dependencies are present we want to avoid copying them to improve performance
                hasDevDependencies = true;
            }

            var productionOnlyPackageInstallCommand = string.Format(
                NodeConstants.ProductionOnlyPackageInstallCommandTemplate, packageInstallCommand);

            if (string.IsNullOrEmpty(_nodeScriptGeneratorOptions.CustomRunBuildCommand))
            {
                var scriptsNode = packageJson?.scripts;
                if (scriptsNode != null)
                {
                    if (scriptsNode.build != null)
                    {
                        runBuildCommand = string.Format(NodeConstants.PkgMgrRunBuildCommandTemplate, packageManagerCmd);
                    }

                    if (scriptsNode["build:azure"] != null && !_commonOptions.ShouldPackage)
                    {
                        runBuildAzureCommand = string.Format(
                            NodeConstants.PkgMgrRunBuildAzureCommandTemplate,
                            packageManagerCmd);
                    }
                }
            }

            if (IsBuildRequired(ctx)
                && string.IsNullOrEmpty(_nodeScriptGeneratorOptions.CustomRunBuildCommand)
                && string.IsNullOrEmpty(runBuildCommand)
                && string.IsNullOrEmpty(runBuildAzureCommand))
            {
                throw new NoBuildStepException(
                    "Could not find either 'build' or 'build:azure' node under 'scripts' in package.json. " +
                    "Could not find value for custom run build command using the environment variable " +
                    "key 'RUN_BUILD_COMMAND'.");
            }

            if (packageJson?.dependencies != null)
            {
                var depSpecs = ((JObject)packageJson.dependencies).ToObject<IDictionary<string, string>>();
                _logger.LogDependencies(
                    _commonOptions.PlatformName,
                    ctx.ResolvedNodeVersion,
                    depSpecs.Select(d => d.Key + d.Value));
            }

            if (packageJson?.devDependencies != null)
            {
                var depSpecs = ((JObject)packageJson.devDependencies).ToObject<IDictionary<string, string>>();
                _logger.LogDependencies(
                    _commonOptions.PlatformName,
                    ctx.ResolvedNodeVersion,
                    depSpecs.Select(d => d.Key + d.Value), true);
            }

            string compressNodeModulesCommand = null;
            string compressedNodeModulesFileName = null;
            GetNodeModulesPackOptions(ctx, out compressNodeModulesCommand, out compressedNodeModulesFileName);

            if (!string.IsNullOrWhiteSpace(compressedNodeModulesFileName))
            {
                manifestFileProperties[NodeConstants.NodeModulesFileBuildProperty] = compressedNodeModulesFileName;
            }

            bool pruneDevDependencies = ShouldPruneDevDependencies(ctx);
            string appInsightsInjectCommand = string.Empty;

            GetAppOutputDirPath(packageJson, manifestFileProperties);

            string customRegistryUrl = null;
            if (ctx.Properties != null)
            {
                ctx.Properties.TryGetValue(RegistryUrlPropertyKey, out customRegistryUrl);
                if (!string.IsNullOrWhiteSpace(customRegistryUrl))
                {
                    // Write the custom registry to the build manifest
                    manifestFileProperties[$"{NodeConstants.PlatformName}_{RegistryUrlPropertyKey}"] = customRegistryUrl;
                }
            }

            var scriptProps = new NodeBashBuildSnippetProperties
            {
                PackageRegistryUrl = customRegistryUrl,
                PackageInstallCommand = packageInstallCommand,
                NpmRunBuildCommand = runBuildCommand,
                NpmRunBuildAzureCommand = runBuildAzureCommand,
                HasProdDependencies = hasProdDependencies,
                HasDevDependencies = hasDevDependencies,
                ProductionOnlyPackageInstallCommand = productionOnlyPackageInstallCommand,
                CompressNodeModulesCommand = compressNodeModulesCommand,
                CompressedNodeModulesFileName = compressedNodeModulesFileName,
                ConfigureYarnCache = configureYarnCache,
                PruneDevDependencies = pruneDevDependencies,
                AppInsightsInjectCommand = appInsightsInjectCommand,
                AppInsightsPackageName = NodeConstants.NodeAppInsightsPackageName,
                AppInsightsLoaderFileName = NodeAppInsightsLoader.NodeAppInsightsLoaderFileName,
                PackageInstallerVersionCommand = packageInstallerVersionCommand,
                RunNpmPack = _commonOptions.ShouldPackage,
                CustomRunBuildCommand = _nodeScriptGeneratorOptions.CustomRunBuildCommand,
            };

            string script = TemplateHelper.Render(
                TemplateHelper.TemplateResource.NodeBuildSnippet,
                scriptProps,
                _logger);

            return new BuildScriptSnippet
            {
                BashBuildScriptSnippet = script,
                BuildProperties = manifestFileProperties,
                PlatformInstallationScriptSnippet = installationScriptSnippet,
            };
        }

        /// <inheritdoc/>
        public bool IsCleanRepo(ISourceRepo repo)
        {
            return !repo.DirExists(NodeConstants.NodeModulesDirName);
        }

        /// <inheritdoc/>
        public bool IsEnabled(RepositoryContext ctx)
        {
            return _commonOptions.EnableNodeJSBuild;
        }

        /// <inheritdoc/>
        public bool IsEnabledForMultiPlatformBuild(RepositoryContext ctx)
        {
            return true;
        }

        /// <inheritdoc/>
        public void SetRequiredTools(
            ISourceRepo sourceRepo,
            string targetPlatformVersion,
            IDictionary<string, string> toolsToVersion)
        {
            Debug.Assert(toolsToVersion != null, $"{nameof(toolsToVersion)} must not be null");
            Debug.Assert(
                sourceRepo != null,
                $"{nameof(sourceRepo)} must not be null since Node needs access to the repository");
            if (!string.IsNullOrWhiteSpace(targetPlatformVersion))
            {
                toolsToVersion[ToolNameConstants.NodeName] = targetPlatformVersion;
            }
        }

        /// <inheritdoc/>
        public void SetVersion(BuildScriptGeneratorContext context, string version)
        {
            context.ResolvedNodeVersion = version;
        }

        /// <inheritdoc/>
        public string GenerateBashRunTimeInstallationScript(RunTimeInstallationScriptGeneratorOptions options)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetDirectoriesToExcludeFromCopyToBuildOutputDir(BuildScriptGeneratorContext ctx)
        {
            var dirs = new List<string>
            {
                NodeConstants.AllNodeModulesDirName,
                NodeConstants.ProdNodeModulesDirName,
            };

            // If the node modules folder is being packaged in a file, we don't copy it to the output
            if (GetNodeModulesPackOptions(ctx, out _, out string compressedFileName))
            {
                // we need to make sure we are not copying the root's node_modules folder
                // if there are any other node_modules folder we will copy them to destination
                dirs.Add(string.Concat("/", NodeConstants.NodeModulesDirName));
            }
            else if (!string.IsNullOrWhiteSpace(compressedFileName))
            {
                dirs.Add(compressedFileName);
            }

            return dirs;
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetDirectoriesToExcludeFromCopyToIntermediateDir(BuildScriptGeneratorContext ctx)
        {
            return new[]
            {
                NodeConstants.AllNodeModulesDirName,
                NodeConstants.ProdNodeModulesDirName,

                // we need to make sure we are not copying the root's node_modules folder
                // if there are any other node_modules folder we will copy them to destination
                string.Concat("/", NodeConstants.NodeModulesDirName),
                NodeConstants.NodeModulesToBeDeletedName,
                NodeConstants.NodeModulesZippedFileName,
                NodeConstants.NodeModulesTarGzFileName,
            };
        }

        /// <summary>
        /// Gets the package json object.
        /// </summary>
        /// <param name="sourceRepo">The source repository.</param>
        /// <param name="logger">The logger of Node.js platform.</param>
        /// <returns>Package json Object.</returns>
        internal static dynamic GetPackageJsonObject(ISourceRepo sourceRepo, ILogger logger)
        {
            dynamic packageJson = null;
            try
            {
                packageJson = sourceRepo.ReadJsonObjectFromFile(NodeConstants.PackageJsonFileName);
            }
            catch (Exception exc)
            {
                // Leave malformed package.json files for Node.js to handle.
                // This prevents Oryx from erroring out when Node.js itself might be able to tolerate the file.
                logger.LogWarning(
                    exc,
                    $"Exception caught while trying to deserialize {NodeConstants.PackageJsonFileName.Hash()}");
            }

            return packageJson;
        }

        private static bool ShouldPruneDevDependencies(BuildScriptGeneratorContext context)
        {
            return BuildPropertiesHelper.IsTrue(PruneDevDependenciesPropertyKey, context, valueIsRequired: false);
        }

        private static bool IsBuildRequired(BuildScriptGeneratorContext context)
        {
            return BuildPropertiesHelper.IsTrue(RequireBuildPropertyKey, context, valueIsRequired: false);
        }

        private static bool DoesPackageDependencyExist(dynamic packageJson, string packageName)
        {
            if (packageJson?.dependencies != null)
            {
                JObject deps = packageJson.dependencies;
                var pkgDependencies = deps.ToObject<IDictionary<string, string>>();
                if (pkgDependencies.ContainsKey(packageName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool GetNodeModulesPackOptions(
            BuildScriptGeneratorContext context,
            out string compressNodeModulesCommand,
            out string compressedNodeModulesFileName)
        {
            var isNodeModulesPackaged = false;
            compressNodeModulesCommand = null;
            compressedNodeModulesFileName = null;
            if (context.Properties != null &&
                context.Properties.TryGetValue(CompressNodeModulesPropertyKey, out string compressNodeModulesOption))
            {
                // default to tar.gz if the property was provided with no value.
                if (string.IsNullOrEmpty(compressNodeModulesOption) ||
                    compressNodeModulesOption.EqualsIgnoreCase(TarGzNodeModulesOption))
                {
                    compressedNodeModulesFileName = NodeConstants.NodeModulesTarGzFileName;
                    compressNodeModulesCommand = $"tar -zcf";
                    isNodeModulesPackaged = true;
                }
                else if (compressNodeModulesOption.EqualsIgnoreCase(ZipNodeModulesOption))
                {
                    compressedNodeModulesFileName = NodeConstants.NodeModulesZippedFileName;
                    compressNodeModulesCommand = $"zip -y -q -r";
                    isNodeModulesPackaged = true;
                }
            }

            return isNodeModulesPackaged;
        }

        private void GetAppOutputDirPath(dynamic packageJson, Dictionary<string, string> buildProperties)
        {
            if (packageJson == null || packageJson.scripts == null || packageJson.scripts["build"] == null)
            {
                return;
            }

            var buildNode = packageJson.scripts["build"] as JValue;
            var buildCommand = buildNode.Value as string;

            if (string.IsNullOrEmpty(buildCommand))
            {
                return;
            }

            string outputDirPath = null;
            if (buildCommand.Contains("ng build", StringComparison.OrdinalIgnoreCase))
            {
                outputDirPath = "dist";
            }
            else if (buildCommand.Contains("gatsby build", StringComparison.OrdinalIgnoreCase))
            {
                outputDirPath = "public";
            }
            else if (buildCommand.Contains("react-scripts build", StringComparison.OrdinalIgnoreCase))
            {
                outputDirPath = "build";
            }
            else if (buildCommand.Contains("next build", StringComparison.OrdinalIgnoreCase))
            {
                outputDirPath = ".next";
            }
            else if (buildCommand.Contains("nuxt build", StringComparison.OrdinalIgnoreCase))
            {
                outputDirPath = ".nuxt";
            }
            else if (buildCommand.Contains("vue-cli-service build", StringComparison.OrdinalIgnoreCase))
            {
                outputDirPath = "dist";
            }
            else if (buildCommand.Contains("hexo generate", StringComparison.OrdinalIgnoreCase))
            {
                outputDirPath = "public";
            }

            if (!string.IsNullOrEmpty(outputDirPath))
            {
                buildProperties[NodeManifestFilePropertyKeys.OutputDirPath] = outputDirPath;
            }
        }
    }
}