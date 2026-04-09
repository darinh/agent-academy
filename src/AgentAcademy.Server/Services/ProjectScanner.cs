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
        var (isGitRepo, gitBranch, repoUrl, defaultBranch, hostProvider) = DetectGit(resolved);
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
            DetectedFiles: detectedFiles.Distinct().OrderBy(f => f).ToList(),
            RepositoryUrl: repoUrl,
            DefaultBranch: defaultBranch,
            HostProvider: hostProvider
        );
    }

    // ── Project Name Detection (priority order) ─────────────────

    private static string? DetectProjectName(string dirPath)
    {
        string? raw = null;

        // 1. package.json
        var pkg = ReadJson(Path.Combine(dirPath, "package.json"));
        if (pkg?.TryGetProperty("name", out var nameEl) == true
            && nameEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            raw = nameEl.GetString();
        }

        // 2. Cargo.toml — [package] ... name = "..."
        if (raw is null)
        {
            var cargo = ReadText(Path.Combine(dirPath, "Cargo.toml"));
            if (cargo is not null)
            {
                var m = Regex.Match(cargo, @"\[package\][^\[]*?name\s*=\s*""([^""]+)""", RegexOptions.Singleline);
                if (m.Success) raw = m.Groups[1].Value;
            }
        }

        // 3. pyproject.toml — name = "..."
        if (raw is null)
        {
            var pyproj = ReadText(Path.Combine(dirPath, "pyproject.toml"));
            if (pyproj is not null)
            {
                var m = Regex.Match(pyproj, @"name\s*=\s*""([^""]+)""");
                if (m.Success) raw = m.Groups[1].Value;
            }
        }

        // 4. go.mod — module <path>
        if (raw is null)
        {
            var gomod = ReadText(Path.Combine(dirPath, "go.mod"));
            if (gomod is not null)
            {
                var m = Regex.Match(gomod, @"^module\s+(\S+)", RegexOptions.Multiline);
                if (m.Success) raw = m.Groups[1].Value;
            }
        }

        // 5. Fallback to directory basename
        if (raw is null)
        {
            var basename = Path.GetFileName(dirPath);
            raw = string.IsNullOrEmpty(basename) ? null : basename;
        }

        return raw is not null ? HumanizeProjectName(raw) : null;
    }

    /// <summary>
    /// Converts package-manager-style identifiers (kebab-case, snake_case, scoped @org/name)
    /// into human-readable title case. E.g. "agent-academy" → "Agent Academy",
    /// "my_cool_app" → "My Cool App", "@scope/my-lib" → "My Lib".
    /// </summary>
    internal static string HumanizeProjectName(string raw)
    {
        var name = raw;

        // Strip npm scopes: @scope/name → name
        var slashIdx = name.LastIndexOf('/');
        if (slashIdx >= 0)
            name = name[(slashIdx + 1)..];

        // Replace hyphens and underscores with spaces, then title-case each word
        var words = name.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        }

        var result = string.Join(' ', words);
        return string.IsNullOrWhiteSpace(result) ? raw : result;
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

    private static (bool IsGitRepo, string? GitBranch, string? RemoteUrl, string? DefaultBranch, string? HostProvider) DetectGit(string dirPath)
    {
        if (!Directory.Exists(Path.Combine(dirPath, ".git")))
            return (false, null, null, null, null);

        string? branch = null;
        string? remoteUrl = null;
        string? defaultBranch = null;

        try { branch = RunGitQuiet(dirPath, "rev-parse", "--abbrev-ref", "HEAD"); }
        catch { }

        try { remoteUrl = RunGitQuiet(dirPath, "remote", "get-url", "origin"); }
        catch { }

        try
        {
            var refs = RunGitQuiet(dirPath, "branch", "--list", "develop", "main", "master");
            if (refs is not null)
            {
                var branches = refs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(b => b.Trim().TrimStart('*').Trim())
                    .ToList();
                if (branches.Contains("develop")) defaultBranch = "develop";
                else if (branches.Contains("main")) defaultBranch = "main";
                else if (branches.Contains("master")) defaultBranch = "master";
            }
        }
        catch { }

        var hostProvider = ParseHostProvider(remoteUrl);
        return (true, branch, remoteUrl, defaultBranch, hostProvider);
    }

    internal static string? ParseHostProvider(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var url = remoteUrl.ToLowerInvariant();
        if (url.Contains("github.com")) return "github";
        if (url.Contains("dev.azure.com") || url.Contains("visualstudio.com")) return "azure-devops";
        if (url.Contains("gitlab.com") || url.Contains("gitlab")) return "gitlab";
        if (url.Contains("bitbucket.org") || url.Contains("bitbucket")) return "bitbucket";
        return null;
    }

    private static string? RunGitQuiet(string workingDir, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(); } catch { }
            return null;
        }
        if (process.ExitCode != 0) return null;
        var output = process.StandardOutput.ReadToEnd().Trim();
        return string.IsNullOrEmpty(output) ? null : output;
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
