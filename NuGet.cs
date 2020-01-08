using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpScriptRunner
{
    static class NuGet
    {
        public static async Task LoadPackage(string packageId, string packageVersion, HashSet<string> buildReferences, HashSet<string> runtimeReferences)
        {
            Console.WriteLine($"Loading package '{packageId} ({packageVersion})'...");
            var version = NuGetVersion.Parse(packageVersion);
            var nuGetFramework = NuGetFramework.ParseFrameworkName(AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName, DefaultFrameworkNameProvider.Instance);
            var settings = Settings.LoadDefaultSettings(null);
            var sourceRepositoryProvider = new SourceRepositoryProvider(new PackageSourceProvider(settings), Repository.Provider.GetCoreV3());
            var packagesPath = SettingsUtility.GetGlobalPackagesFolder(settings);
            var runtimeFramework = NuGetFramework.ParseFolder(BuildInfo.RuntimeIdentifier);

            using (var cacheContext = new SourceCacheContext())
            {
                var repositories = sourceRepositoryProvider.GetRepositories();
                var availablePackages = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
                await GetPackageDependencies(
                    new PackageIdentity(packageId, version),
                    nuGetFramework, cacheContext, NullLogger.Instance, repositories, availablePackages);

                var resolverContext = new PackageResolverContext(
                    DependencyBehavior.Lowest,
                    new[] { packageId },
                    Enumerable.Empty<string>(),
                    Enumerable.Empty<PackageReference>(),
                    Enumerable.Empty<PackageIdentity>(),
                    availablePackages,
                    sourceRepositoryProvider.GetRepositories().Select(s => s.PackageSource),
                    NullLogger.Instance);

                var resolver = new PackageResolver();
                var packagesToInstall = resolver.Resolve(resolverContext, CancellationToken.None)
                    .Select(p => availablePackages.Single(x => PackageIdentityComparer.Default.Equals(x, p)));
                var packagePathResolver = new PackagePathResolver(packagesPath, true);
                var packageExtractionContext = new PackageExtractionContext(
                    PackageSaveMode.Defaultv3,
                    XmlDocFileSaveMode.None,
                    ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance),
                    NullLogger.Instance);

                var frameworkReducer = new FrameworkReducer();

                foreach (var packageToInstall in packagesToInstall)
                {
                    var installedPath = packagePathResolver.GetInstalledPath(packageToInstall);
                    if (installedPath == null)
                    {
                        Console.WriteLine($"Installing package '{packageToInstall.Id} ({packageToInstall.Version})'...");
                        var downloadResource = await packageToInstall.Source.GetResourceAsync<DownloadResource>(CancellationToken.None);
                        var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                            packageToInstall,
                            new PackageDownloadContext(cacheContext),
                            packagesPath,
                            NullLogger.Instance, CancellationToken.None);

                        await PackageExtractor.ExtractPackageAsync(
                            downloadResult.PackageSource,
                            downloadResult.PackageStream,
                            packagePathResolver,
                            packageExtractionContext,
                            CancellationToken.None);
                    }

                    installedPath = packagePathResolver.GetInstalledPath(packageToInstall);
                    var packageReader = new PackageFolderReader(installedPath);

                    if (buildReferences != null)
                    {
                        var items = await packageReader.GetReferenceItemsAsync(CancellationToken.None);
                        var paths = GetAssemblyPaths(items, frameworkReducer, nuGetFramework, installedPath);
                        buildReferences.AddRange(paths);
                    }

                    if (runtimeReferences != null)
                    {
                        var items = await packageReader.GetItemsAsync("runtimes", CancellationToken.None);
                        var nearest = items.Select(x => x.TargetFramework)
                            .Where(x => x.Framework == runtimeFramework.Framework && (!x.HasProfile || x.Profile == runtimeFramework.Profile))
                            .OrderBy(x => x.HasProfile ? 1 : -1)
                            .FirstOrDefault();

                        var runtimeDir = items.FirstOrDefault(x => x.TargetFramework.Equals(nearest))?.Items.FirstOrDefault()?.Split('/').ElementAtOrDefault(1);
                        if (runtimeDir != null)
                        {
                            installedPath = Path.Combine(installedPath, "runtimes", runtimeDir);
                            packageReader = new PackageFolderReader(installedPath);
                        }

                        items = await packageReader.GetLibItemsAsync(CancellationToken.None);
                        var paths = GetAssemblyPaths(items, frameworkReducer, nuGetFramework, installedPath);
                        runtimeReferences.AddRange(paths);
                    }
                }
            }
        }

        static async Task GetPackageDependencies(
            PackageIdentity package,
            NuGetFramework framework,
            SourceCacheContext cacheContext,
            ILogger logger,
            IEnumerable<SourceRepository> repositories,
            ISet<SourcePackageDependencyInfo> availablePackages)
        {
            if (availablePackages.Contains(package)) return;

            foreach (var sourceRepository in repositories)
            {
                var dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>();
                var dependencyInfo = await dependencyInfoResource.ResolvePackage(
                    package, framework, cacheContext, logger, CancellationToken.None);

                if (dependencyInfo == null) continue;

                availablePackages.Add(dependencyInfo);
                foreach (var dependency in dependencyInfo.Dependencies)
                {
                    await GetPackageDependencies(
                        new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion),
                        framework, cacheContext, logger, repositories, availablePackages);
                }
            }
        }


        static IEnumerable<string> GetAssemblyPaths(IEnumerable<FrameworkSpecificGroup> items, FrameworkReducer frameworkReducer, NuGetFramework nuGetFramework, string installedPath)
        {
            var nearest = frameworkReducer.GetNearest(nuGetFramework, items.Select(x => x.TargetFramework));
            return items
                .Where(x => x.TargetFramework.Equals(nearest))
                .SelectMany(x => x.Items)
                .Where(x => x.EndsWith(".dll"))
                .Select(x => Path.Combine(installedPath, x));
        }
    }
}