namespace Scripts;

using System.Collections.Generic;
using System.Text.RegularExpressions;
using ScriptsBase.Checks;

public class CodeChecks : CodeChecksBase<Program.CheckOptions>
{
    public CodeChecks(Program.CheckOptions opts) : base(opts)
    {
        FilePathsToAlwaysIgnore.Add(new Regex(@"\.Designer\.cs"));
        FilePathsToAlwaysIgnore.Add(new Regex(@"version_data\/"));
    }

    protected override Dictionary<string, CodeCheck> ValidChecks { get; } = new()
    {
        { "files", new FileChecks() },
        { "compile", new CompileCheck() },
        { "inspectcode", new InspectCode() },
        { "cleanupcode", new CleanupCode() },
    };

    protected override IEnumerable<string> ExtraIgnoredJetbrainsInspectWildcards => new[] { "*.Designer.cs" };

    protected override string MainSolutionFile => "ThriveLauncher.sln";
}
