namespace Scripts;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScriptsBase.Models;
using ScriptsBase.ToolBases;
using ScriptsBase.Utilities;
using SharedBase.Models;
using SharedBase.Utilities;

public class PackageTool : PackageToolBase<Program.PackageOptions>
{
    public const string DotnetInstallerName = "windowsdesktop-runtime-6.0.11-win-x64.exe";
    public const string PathToDotnetInstaller = $"DependencyInstallers/{DotnetInstallerName}";

    private const string BuilderImageName = "localhost/thrive/launcher-builder:latest";
    private const string LauncherCsproj = "ThriveLauncher/ThriveLauncher.csproj";
    private const string LauncherExecutableIconFile = "ThriveLauncher/Assets/Icons/icon.ico";
    private const string LauncherInstallerBannerImageFile = "Scripts/installer_banner.bmp";
    private const string LauncherInstallerLicenseFile = "LICENSE.md";
    private const string NoRuntimeSuffix = "_without_runtime";

    private const string NSISFileName = "launcher.nsi";
    private const string NSISDotnetInstallerFileName = "launcher_dotnet_installer.nsi";
    private const string NSISTemplateFile = $"Scripts/{NSISFileName}.template";

    private static readonly IReadOnlyList<PackagePlatform> LauncherPlatforms = new List<PackagePlatform>
    {
        PackagePlatform.Linux,
        PackagePlatform.Windows,
        PackagePlatform.Mac,
    };

    private static readonly IReadOnlyCollection<FileToPackage> ExtraFilesToPackage = new List<FileToPackage>
    {
        new("LICENSE.md", "LICENSE.md"),
        new("ThriveLauncher/ThriveLauncher.desktop", "ThriveLauncher.desktop", PackagePlatform.Linux),
        new("ThriveLauncher/launcher-icon.png", "thrive-launcher-icon.png", PackagePlatform.Linux),
    };

    private static readonly IReadOnlyCollection<string> SourceItemsToPackage = new List<string>
    {
        "ThriveLauncher.sln",
        "ThriveLauncher.sln.DotSettings",
        "ThriveLauncher",
        "Tests",
        "LauncherBackend",
        "tools",
        "LICENSE.md",
        "README.md",
        "RevolutionaryGamesCommon",
    };

    private readonly string launcherVersion;

    /// <summary>
    ///   NSIS requires 4 numbers in the version always
    /// </summary>
    private readonly string launcherVersionAlwaysWithRevision;

    private bool doingNoRuntimeExport;

    public PackageTool(Program.PackageOptions options) : base(options)
    {
        // Retries don't really work for us so set it to 0
        options.Retries = 0;

        // Mac builds need to be done on a mac
        if (OperatingSystem.IsMacOS())
        {
            DefaultPlatforms = new[] { PackagePlatform.Mac };
        }
        else
        {
            DefaultPlatforms = LauncherPlatforms.Where(p => p != PackagePlatform.Mac).ToList();
        }

        launcherVersion = AssemblyInfoReader.ReadVersionFromCsproj(LauncherCsproj);

        var parsedVersion = new Version(launcherVersion);

        if (parsedVersion.Revision <= 0)
            parsedVersion = new Version(parsedVersion.Major, parsedVersion.Minor, parsedVersion.Build, 0);

        launcherVersionAlwaysWithRevision = parsedVersion.ToString();
    }

    protected override IReadOnlyCollection<PackagePlatform> ValidPlatforms => LauncherPlatforms;

    protected override IEnumerable<PackagePlatform> DefaultPlatforms { get; }

    protected override IEnumerable<string> SourceFilesToPackage => SourceItemsToPackage;

    private string ReadmeFile => Path.Join(options.OutputFolder, "README.txt");
    private string RevisionFile => Path.Join(options.OutputFolder, "revision.txt");

    private string NSISInstallerName => doingNoRuntimeExport ?
        $"ThriveLauncher_Windows-7_Installer_{launcherVersionAlwaysWithRevision}.exe" :
        $"ThriveLauncher_Windows_Installer_{launcherVersionAlwaysWithRevision}.exe";

    private string ExpectedLauncherInstallerFile => Path.Join(options.OutputFolder, NSISInstallerName);

    protected override async Task<bool> OnBeforeStartExport(CancellationToken cancellationToken)
    {
        if (options.LinuxPodman == true)
        {
            ColourConsole.WriteNormalLine("Podman will be used for Linux builds");
        }

        if (options.CreateDynamicFiles == true)
        {
            await CreateDynamicallyGeneratedFiles(cancellationToken);
        }
        else
        {
            ColourConsole.WriteWarningLine("Skipping dynamic file generation");
        }

        return true;
    }

    protected override string GetFolderNameForExport(PackagePlatform platform)
    {
        var name = ThriveProperties.GetFolderNameForLauncher(platform, launcherVersion);

        if (doingNoRuntimeExport)
            name = $"{name}{NoRuntimeSuffix}";

        return name;
    }

    protected override string GetCompressedExtensionForPlatform(PackagePlatform platform)
    {
        return $"_standalone{base.GetCompressedExtensionForPlatform(platform)}";
    }

    protected override async Task<bool> PackageForPlatform(CancellationToken cancellationToken,
        PackagePlatform platform)
    {
        if (!await base.PackageForPlatform(cancellationToken, platform))
            return false;

        if (options.CreateWindowsNoRuntime == true)
        {
            ColourConsole.WriteInfoLine($"Doing a no runtime variant of export for {platform}");
            doingNoRuntimeExport = true;

            if (!await base.PackageForPlatform(cancellationToken, platform))
                return false;

            doingNoRuntimeExport = false;
            ColourConsole.WriteInfoLine($"No runtime variant succeeded");
        }

        return true;
    }

    protected override async Task<bool> Export(PackagePlatform platform, string folder,
        CancellationToken cancellationToken)
    {
        ColourConsole.WriteInfoLine($"Starting dotnet publish for platform: {platform}");
        Directory.CreateDirectory(folder);

        if (platform == PackagePlatform.Mac)
        {
            // TODO: do the two builds and combine them (as there doesn't seem to be a combined way)
            // dotnet publish -c Release -r osx-x64 --self-contained true -o dist/mac-x64 ThriveLauncher
            // dotnet publish -c Release -r osx-arm64 --self-contained true -o dist/mac-arm ThriveLauncher

            throw new NotImplementedException();
        }
        else if (platform == PackagePlatform.Linux && options.LinuxPodman == true)
        {
            ColourConsole.WriteInfoLine("Attempting Linux build in podman");
            var folderName = Path.GetFileName(folder);

            var baseFolder = Path.GetFullPath(".");

            var podmanCommands = new List<string>
            {
                "set -e",
                "echo 'setting up build folder'",
                "mkdir /build",
                "rsync -ah /source/ /build/ --exclude builds --exclude bin --exclude obj --exclude .git",
                "echo 'copying succeeded'",
                "echo 'building...'",

                // Need to configure the build in the right way to do just the build we want in the container
                "cd /build && dotnet run --project Scripts -- package " +
                $"--podman false --compress false --installers false --dynamic-files false {platform}",
                "echo 'build finished'",
                "echo 'copying result'",
                $"rsync -vhr '/build/builds/{folderName}/' /out/ --delete",
                "echo 'result copied'",
            };

            var startInfo = new ProcessStartInfo("podman");
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--rm");

            // startInfo.ArgumentList.Add("--name=test");
            startInfo.ArgumentList.Add("-i");

            // Source mounted in read only way
            startInfo.ArgumentList.Add("--mount");
            startInfo.ArgumentList.Add($"type=bind,source={baseFolder},destination=/source,relabel=shared,ro=true");

            // Output folder mount allows writing
            startInfo.ArgumentList.Add("--mount");
            startInfo.ArgumentList.Add($"type=bind,source={folder},destination=/out,relabel=shared");

            startInfo.ArgumentList.Add(BuilderImageName);
            startInfo.ArgumentList.Add("/bin/bash");

            ColourConsole.WriteNormalLine("### Beginning podman build, following output is from the recursive build:");

            var result = await ProcessRunHelpers.RunProcessWithStdInAndOutputStreamingAsync(startInfo,
                cancellationToken, podmanCommands, ContainerOutput, ContainerOutput);

            if (result.ExitCode != 0)
            {
                ColourConsole.WriteWarningLine("Running podman failed. Has the build image been built?");
                return false;
            }

            ColourConsole.WriteNormalLine("###");
            ColourConsole.WriteNormalLine("### Podman run succeeded");
            ColourConsole.WriteNormalLine("###");
        }
        else
        {
            string runtime;
            bool canPublishWithNoRuntime = false;

            switch (platform)
            {
                case PackagePlatform.Linux:
                    runtime = "linux-x64";
                    break;
                case PackagePlatform.Windows:
                    runtime = "win-x64";
                    canPublishWithNoRuntime = true;
                    break;
                case PackagePlatform.Windows32:
                    runtime = "win-x86";
                    canPublishWithNoRuntime = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
            }

            if (!await RunPublish(folder, runtime, !canPublishWithNoRuntime || !doingNoRuntimeExport,
                    cancellationToken))
            {
                return false;
            }
        }

        ColourConsole.WriteSuccessLine("Publish succeeded");
        return true;
    }

    protected override async Task<bool> OnPostProcessExportedFolder(PackagePlatform platform, string folder,
        CancellationToken cancellationToken)
    {
        if (platform == PackagePlatform.Mac)
        {
            // Maybe some folder cleanup here?
        }
        else if (platform is PackagePlatform.Windows or PackagePlatform.Windows32)
        {
            if (!OperatingSystem.IsWindows())
            {
                ColourConsole.WriteInfoLine("Attempting to manually set right executable flags and metadata");

                await PostProcessWindowsFolder(folder, cancellationToken);
            }
            else
            {
                ColourConsole.WriteNormalLine("Assuming export on Windows already set right executable properties");
            }
        }

        PrunePdbFiles(folder);

        return true;
    }

    protected override async Task<bool> OnPostFolderHandled(PackagePlatform platform, string folderOrArchive,
        CancellationToken cancellationToken)
    {
        if (options.CreateInstallers == true)
        {
            ColourConsole.WriteInfoLine($"Creating installer for {platform} from {folderOrArchive}");

            if (platform == PackagePlatform.Linux)
            {
                ColourConsole.WriteInfoLine("Linux installer is made with flatpak (hosted on Flathub)");
                AddReprintMessage("Linux installer needs to be separate updated for Flathub");
            }
            else if (platform is PackagePlatform.Windows or PackagePlatform.Windows32)
            {
                if (platform == PackagePlatform.Windows32)
                {
                    throw new NotImplementedException(
                        "Windows32 installer needs a suffix or something to not conflict");
                }

                if (options.CreateWindowsNoRuntime != true)
                    throw new NotImplementedException("Windows installers without runtime is not implemented");

                var potentialExtension = GetCompressedExtensionForPlatform(platform);

                string nsisSource = folderOrArchive;

                if (folderOrArchive.EndsWith(potentialExtension))
                    nsisSource = nsisSource.Substring(0, nsisSource.Length - potentialExtension.Length);

                var nsisFileName = NSISFileName;
                var nsisTemplate = NSISTemplateFile;

                if (doingNoRuntimeExport)
                {
                    nsisFileName = NSISDotnetInstallerFileName;

                    // TODO: remove the constant if it doesn't turn out useful
                    // nsisTemplate = NSISTemplateDotnetInstallerFile;
                }

                // Windows installer is made with NSIS
                await GenerateNSISFile(nsisSource, nsisFileName, nsisTemplate, cancellationToken);
                await RunNSIS(nsisFileName, cancellationToken);

                if (!File.Exists(ExpectedLauncherInstallerFile))
                {
                    ColourConsole.WriteErrorLine("Expected installer file did not get created");
                    return false;
                }

                var hash = FileUtilities.HashToHex(
                    await FileUtilities.CalculateSha3OfFile(ExpectedLauncherInstallerFile, cancellationToken));

                var message1 = $"Created {platform} installer: {ExpectedLauncherInstallerFile}";
                var message2 = $"SHA3: {hash}";

                AddReprintMessage(string.Empty);
                AddReprintMessage(message1);
                AddReprintMessage(message2);

                ColourConsole.WriteSuccessLine(message1);
                ColourConsole.WriteNormalLine(message2);
            }
            else
            {
                ColourConsole.WriteErrorLine($"TODO installer creation");
                throw new NotImplementedException();
            }
        }

        return true;
    }

    protected override IEnumerable<FileToPackage> GetFilesToPackage()
    {
        if (options.CreateDynamicFiles == true)
        {
            yield return new FileToPackage(ReadmeFile, "README.txt");
            yield return new FileToPackage(RevisionFile, "revision.txt");
        }

        foreach (var fileToPackage in ExtraFilesToPackage)
        {
            yield return fileToPackage;
        }
    }

    private async Task CreateDynamicallyGeneratedFiles(CancellationToken cancellationToken)
    {
        await using var readme = File.CreateText(ReadmeFile);

        await readme.WriteLineAsync("Thrive Launcher");
        await readme.WriteLineAsync(string.Empty);
        await readme.WriteLineAsync(
            "This is a release of the Thrive Launcher. Run the executable 'ThriveLauncher' to open.");
        await readme.WriteLineAsync("The launcher allows downloading and playing available Thrive versions.");
        await readme.WriteLineAsync(string.Empty);
        await readme.WriteLineAsync(
            "Source code is available online: https://github.com/Revolutionary-Games/Thrive-Launcher");
        await readme.WriteLineAsync(string.Empty);
        await readme.WriteLineAsync("Exact commit this build is made from is in revision.txt");

        cancellationToken.ThrowIfCancellationRequested();

        await using var revision = File.CreateText(RevisionFile);

        await revision.WriteLineAsync(await GitRunHelpers.Log("./", 1, cancellationToken));
        await revision.WriteLineAsync(string.Empty);

        var diff = (await GitRunHelpers.Diff("./", cancellationToken, false, false)).Trim();

        if (!string.IsNullOrEmpty(diff))
        {
            await readme.WriteLineAsync("dirty, diff:");
            await readme.WriteLineAsync(diff);
        }
    }

    private async Task<bool> RunPublish(string folder, string runtime, bool selfContained,
        CancellationToken cancellationToken)
    {
        ColourConsole.WriteNormalLine($"Publishing to folder: {folder}");

        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(runtime);
        startInfo.ArgumentList.Add("--self-contained");

        if (selfContained)
        {
            startInfo.ArgumentList.Add("true");
        }
        else
        {
            startInfo.ArgumentList.Add("false");
        }

        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(folder);
        startInfo.ArgumentList.Add("ThriveLauncher");

        var result = await ProcessRunHelpers.RunProcessAsync(startInfo, cancellationToken, false);

        if (result.ExitCode != 0)
        {
            ColourConsole.WriteWarningLine("Publishing with dotnet failed");
            return false;
        }

        return true;
    }

    private async Task PostProcessWindowsFolder(string folder, CancellationToken cancellationToken)
    {
        var versionData = AssemblyInfoReader.ReadAllProjectVersionMetadata(LauncherCsproj);
        var executable = Path.Join(folder, "ThriveLauncher.exe");

        await RunRcEdit(executable, cancellationToken, "--set-icon", LauncherExecutableIconFile,
            "--set-version-string", "ProductName", "Thrive Launcher",
            "--set-version-string", "CompanyName", versionData.Authors,
            "--set-version-string", "FileDescription", versionData.Description,
            "--set-version-string", "LegalCopyright", versionData.Copyright,
            "--set-version-string", "FileVersion", versionData.Version,
            "--set-version-string", "ProductVersion", versionData.Version);

        // This seems to require setting separately to stick
        await RunRcEdit(executable, cancellationToken, "--set-product-version", versionData.Version);

        // TODO: setting the executable date?

        // TODO: put back
        // using var modifier = new PEModifier(executable);
        //
        // await modifier.SetExecutableToGUIMode(cancellationToken);

        ColourConsole.WriteNormalLine($"Executable ({executable}) modified");
    }

    private void PrunePdbFiles(string folder)
    {
        ColourConsole.WriteNormalLine($"Pruning .pdb files in {folder}");

        foreach (var file in Directory.EnumerateFiles(folder, "*.pdb", SearchOption.AllDirectories))
        {
            ColourConsole.WriteDebugLine($"Removing pdb file: {file}");
            File.Delete(file);
        }
    }

    private void ContainerOutput(string line)
    {
        ColourConsole.WriteNormalLine($" {line}");
    }

    private async Task RunRcEdit(string executable, CancellationToken cancellationToken, params string[] arguments)
    {
        ColourConsole.WriteNormalLine($"Running {options.RcEdit} on: {executable}");

        string pathToRcEdit;

        if (!File.Exists(options.RcEdit))
        {
            pathToRcEdit = ExecutableFinder.Which(options.RcEdit) ??
                throw new Exception("Could not find rcedit in PATH");
        }
        else
        {
            pathToRcEdit = options.RcEdit;
        }

        ProcessStartInfo startInfo;
        if (!OperatingSystem.IsWindows())
        {
            // It seems to work even without wine, but for clarify of what's happening this will try to run through
            // wine explicitly
            startInfo = new ProcessStartInfo(ExecutableFinder.Which("wine") ??
                throw new Exception("Wine is not installed"));
            startInfo.ArgumentList.Add(pathToRcEdit);
        }
        else
        {
            startInfo = new ProcessStartInfo(pathToRcEdit);
        }

        startInfo.ArgumentList.Add(executable);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = await ProcessRunHelpers.RunProcessAsync(startInfo, cancellationToken, false);

        if (result.ExitCode != 0)
        {
            ColourConsole.WriteWarningLine("Running rcedit failed. Is it installed?");
            throw new Exception($"rcedit exited with {result.ExitCode}");
        }
    }

    private async Task GenerateNSISFile(string sourceFolder, string nsisFileName, string templateFile,
        CancellationToken cancellationToken)
    {
        var target =
            Path.Combine(Path.GetDirectoryName(sourceFolder) ?? throw new ArgumentException("Can't get parent folder"),
                nsisFileName);

        var versionData = AssemblyInfoReader.ReadAllProjectVersionMetadata(LauncherCsproj);

        ColourConsole.WriteNormalLine($"Generating NSIS config at {target} with source folder: {sourceFolder}");

        var templateText = await File.ReadAllTextAsync(templateFile, Encoding.UTF8, cancellationToken);

        var dotnetMode = "# ";
        var installerName = NSISInstallerName;

        if (doingNoRuntimeExport)
        {
            dotnetMode = string.Empty;
        }

        var replacedVariables = new Dictionary<string, string>
        {
            { "REPLACE_TEMPLATE_VERSION", launcherVersionAlwaysWithRevision },
            { "REPLACE_TEMPLATE_ICON_FILE", PrepareNSISPath(LauncherExecutableIconFile) },
            { "REPLACE_TEMPLATE_BANNER_IMAGE_FILE", PrepareNSISPath(LauncherInstallerBannerImageFile) },
            { "REPLACE_TEMPLATE_PATH_TO_LICENSE", PrepareNSISPath(LauncherInstallerLicenseFile) },
            { "REPLACE_TEMPLATE_SOURCE_DIRECTORY", PrepareNSISPath(sourceFolder) },
            { "REPLACE_TEMPLATE_DESCRIPTION", versionData.Description },
            { "REPLACE_TEMPLATE_COPYRIGHT", versionData.Copyright },
            { "REPLACE_TEMPLATE_DOTNET_INSTALLER_NAME", DotnetInstallerName },
            { "REPLACE_TEMPLATE_PATH_TO_DOTNET_INSTALLER", PrepareNSISPath(PathToDotnetInstaller) },
            { "TEMPLATE_MODE_DOTNET;", dotnetMode },
            { "REPLACE_TEMPLATE_INSTALLER_NAME", installerName },
        };

        string finalText = templateText;

        foreach (var (variable, replacingText) in replacedVariables)
        {
            finalText = finalText.Replace(variable, replacingText);
        }

        await File.WriteAllTextAsync(target, finalText, new UTF8Encoding(true), cancellationToken);
    }

    private string PrepareNSISPath(string rawPath)
    {
        var full = Path.GetFullPath(rawPath);

        // Some places only allow double \\ for paths, so to be safe we use that everywhere
        return full.Replace("/", @"\").Replace(@"\", @"\\").Replace(@"\\\", @"\\");
    }

    private async Task RunNSIS(string nsisFileName, CancellationToken cancellationToken)
    {
        ColourConsole.WriteNormalLine($"Running makensis on {Path.Join(options.OutputFolder, nsisFileName)}");

        var startInfo = new ProcessStartInfo("makensis")
        {
            WorkingDirectory = options.OutputFolder,
        };

        startInfo.ArgumentList.Add(nsisFileName);

        var result = await ProcessRunHelpers.RunProcessAsync(startInfo, cancellationToken, false);

        if (result.ExitCode != 0)
        {
            ColourConsole.WriteWarningLine("Running makensis failed. Is it installed?");
            throw new Exception($"makensis exited with {result.ExitCode}");
        }

        ColourConsole.WriteSuccessLine($"Running makensis succeeded");
    }
}
