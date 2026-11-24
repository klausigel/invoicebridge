// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kig.InvoiceBridge;

internal sealed record ToolPaths(string Java, string Ghostscript, string IccProfile, string Mustang, string VeraPdf)
{
    internal static ToolPaths Resolve(BridgeConfiguration settings)
    {
        var tools = settings.ToolsDirectory;
        var windows = OperatingSystem.IsWindows();
        if (!windows && !OperatingSystem.IsLinux())
            throw new BridgeException(20, "Unterstützte Plattformen: Windows und Linux.");
        var ghostscriptRoot = Path.Combine(tools, "ghostscript-10.08.0");
        var paths = new ToolPaths(
            windows ? Path.Combine(tools, "java", "bin", "java.exe") : "/usr/bin/java",
            settings.GhostscriptExecutable ?? (windows ? Path.Combine(ghostscriptRoot, "bin", "gswin64c.exe") : "/usr/bin/gs"),
            windows ? Path.Combine(ghostscriptRoot, "iccprofiles", "default_rgb.icc") : "/usr/share/color/icc/ghostscript/default_rgb.icc",
            Path.Combine(tools, "Mustang-CLI-2.26.0.jar"),
            Path.Combine(tools, "verapdf", "bin", "cli-1.30.3.jar"));
        foreach (var (name, file) in new[] {
            ("Java", paths.Java), ("Ghostscript", paths.Ghostscript), ("ICC-Profil", paths.IccProfile),
            ("Mustang", paths.Mustang), ("veraPDF", paths.VeraPdf)
        })
            if (!File.Exists(file)) throw new BridgeException(20, "Benötigte Abhängigkeit fehlt: " + name);
        return paths;
    }
}
