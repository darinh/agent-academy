using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Scans a local project directory to detect its name, tech stack,
/// git state, and configuration files. Ported from the TypeScript
/// ProjectScanner in local-agent-host.
/// </summary>
public sealed class ProjectScanner
{
    /// <summary>Config files and the tech stack they imply.</summary>
    private static readonly Dictionary<string, string> ConfigFiles = new()
    {
        ["package.json"] = "Node.js",
        ["tsconfig.json"] = "TypeScript",
        ["Cargo.toml"] = "Rust",
        ["go.mod"] = "Go",
        ["pyproject.toml"] = "Python",
        ["requirements.txt"] = "Python",
        ["setup.py"] = "Python",
        ["Gemfile"] = "Ruby",
        ["pom.xml"] = "Java",
        ["build.gradle"] = "Java",
        ["Dockerfile"] = "Docker",
    };

    /// <summary>File extensions matched by scanning top-level directory entries.</summary>
    private static readonly Dictionary<string, string> ExtensionStacks = new()
    {
        [".csproj"] = ".NET",
        [".sln"] = ".NET",
    };

    /// <summary>npm dependency prefixes that map to additional stack entries.</summary>
    private static readonly Dictionary<string, string> NpmStacks = new()
    {
        ["react"] = "React",
        ["express"] = "Express",
        ["vue"] = "Vue",
        ["angular"] = "Angular",
        ["next"] = "Next.js",
        ["vite"] = "Vite",
    };

    /// <summary>
    /// Scans a directory and returns a <see cref="ProjectScanResult"/>.
    /// </summary>
    public ProjectScanResult ScanProject(string dirPath)
    {
        var resolved = Path.GetFullPath(dirPath);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory does not exist: {resolved}");

        var detectedFiles = new List<string>();
        var techStack = DetectTechStack(resolved, detectedFiles);
        var projectName = DetectProjectName(resolved);
        var (isGitRepo, gitBranch) = DetectGit(resolved);
        var hasSpecs = Directory.Exists(Path.Combine(resolved, "specs"));
        var hasReadme = File.Exists(Path.Combine(resolved, "README.md"))
                     || File.Exists(Path.Combine(resolved, "README"));

        return new ProjectScanResult(
            Path: resolved,
            ProjectName: projectName,
            TechStack: techStack,
            HasSpecs: hasSpecs,
            HasReadme: hasReadme,
            IsGitRepo: isGitRepo,
            GitBranch: gitBranch,
            DetectedFiles: detectedFiles.Distinct().OrderBy(f => f).ToList()
        );
    }

    // ── Project Name Detection (priority order) ─────────────────

    private static string? DetectProjectName(string dirPath)
    {
        // 1. package.json
        var pkg = ReadJson(Path.Combine(dirPath, "package.json"));
        if (pkg?.TryGetProperty("name", out var nameEl) == true
            && nameEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            return nameEl.GetString();
        }

        // 2. Cargo.toml — [package] ... name = "..."
        var cargo = ReadText(Path.Combine(dirPath, "Cargo.toml"));
        if (cargo is not null)
        {
            var m = Regex.Match(cargo, @"\[package\][^\[]*?name\s*=\s*""([^""]+)""", RegexOptions.Singleline);
            if (m.Success) return m.Groups[1].Value;
        }

        // 3. pyproject.toml — name = "..."
        var pyproj = ReadText(Path.Combine(dirPath, "pyproject.toml"));
        if (pyproj is not null)
        {
            var m = Regex.Match(pyproj, @"name\s*=\s*""([^""]+)""");
            if (m.Success) return m.Groups[1].Value;
        }

        // 4. go.mod — module <path>
        var gomod = ReadText(Path.Combine(dirPath, "go.mod"));
        if (gomod is not null)
        {
            var m = Regex.Match(gomod, @"^module\s+(\S+)", RegexOptions.Multiline);
            if (m.Success) return m.Groups[1].Value;
        }

        // 5. Fallback to directory basename
        var basename = Path.GetFileName(dirPath);
        return string.IsNullOrEmpty(basename) ? null : basename;
    }

    // ── Tech Stack Detection ────────────────────────────────────

    private static List<string> DetectTechStack(string dirPath, List<string> detectedFiles)
    {
        var stack = new HashSet<string>();

        // Check known config files
        foreach (var (file, tech) in ConfigFiles)
        {
            if (File.Exists(Path.Combine(dirPath, file)))
            {
                stack.Add(tech);
                detectedFiles.Add(file);
            }
        }

        // Check extension-based matches (.csproj, .sln) by scanning top-level entries
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dirPath))
            {
                var name = Path.GetFileName(entry);
                foreach (var (ext, tech) in ExtensionStacks)
                {
                    if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        stack.Add(tech);
                        detectedFiles.Add(name);
                    }
                }
            }
        }
        catch
        {
            // Ignore read errors
        }

        // Inspect npm dependencies for framework detection
        if (stack.Contains("Node.js"))
        {
            var pkg = ReadJson(Path.Combine(dirPath, "package.json"));
            if (pkg.HasValue)
            {
                var depKeys = new List<string>();
                AddDependencyKeys(pkg.Value, "dependencies", depKeys);
                AddDependencyKeys(pkg.Value, "devDependencies", depKeys);

                foreach (var (prefix, tech) in NpmStacks)
                {
                    if (depKeys.Any(d =>
                        d == prefix
                        || d.StartsWith($"{prefix}/", StringComparison.Ordinal)
                        || d.StartsWith($"@{prefix}/", StringComparison.Ordinal)))
                    {
                        stack.Add(tech);
                    }
                }
            }
        }

        return stack.ToList();
    }

    // ── Git Detection ───────────────────────────────────────────

    private static (bool IsGitRepo, string? GitBranch) DetectGit(string dirPath)
    {
        if (!Directory.Exists(Path.Combine(dirPath, ".git")))
            return (false, null);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
                WorkingDirectory = dirPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.Start();

            // Wait with timeout BEFORE reading to avoid deadlock on full buffers.
            // If the process doesn't exit in time, kill it.
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { /* best effort */ }
                return (true, null);
            }

            var branch = process.StandardOutput.ReadToEnd().Trim();
            return (true, string.IsNullOrEmpty(branch) ? null : branch);
        }
        catch
        {
            // .git exists but git command failed — still a repo, branch unknown
            return (true, null);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static JsonElement? ReadJson(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var text = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadText(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddDependencyKeys(JsonElement root, string section, List<string> keys)
    {
        if (root.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in deps.EnumerateObject())
            {
                keys.Add(prop.Name);
            }
        }
    }
}
