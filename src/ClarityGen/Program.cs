﻿using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Myrtus.Clarity.Generator;

public class Program
{
    static async Task Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("ClarityGen").Color(Color.Cyan1));

        if (args.Length == 0)
        {
            var projectName = AnsiConsole.Ask<string>("[yellow]Enter project name:[/]");
            var outputDirectory = AnsiConsole.Ask<string>("[yellow]Enter output directory (or press Enter for current):[/]");

            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = AppDomain.CurrentDomain.BaseDirectory;

            args = new[] { projectName, outputDirectory };
        }

        string newProjectName = args[0];
        string outputDir = args.Length > 1 ? args[1] : AppDomain.CurrentDomain.BaseDirectory;

        await RunGeneratorAsync(newProjectName, outputDir);
    }

    static async Task RunGeneratorAsync(string projectName, string outputDir)
    {
        await AnsiConsole.Status()
            .StartAsync("Initializing...", async ctx =>
            {
                var config = await LoadConfigurationAsync();
                if (config is null) return;

                var generator = new ProjectGenerator(config, ctx);
                await generator.GenerateProjectAsync(projectName, outputDir);
            });
    }

    static async Task<AppSettings?> LoadConfigurationAsync()
    {
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Configuration file not found");
            return null;
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(configPath));

            if (string.IsNullOrEmpty(config?.Template?.GitRepoUrl))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid configuration: Git repo URL is missing");
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration Error:[/] {ex.Message}");
            return null;
        }
    }
}

public class ProjectGenerator
{
    private readonly AppSettings _config;
    private readonly StatusContext _status;
    private readonly string _tempDir;

    public ProjectGenerator(AppSettings config, StatusContext status)
    {
        _config = config;
        _status = status;
        _tempDir = Path.Combine(Path.GetTempPath(), $"ClarityGen-{Guid.NewGuid()}");
    }

    public async Task GenerateProjectAsync(string projectName, string outputDir)
    {
        try
        {
            await CloneTemplateRepositoryAsync();
            await RenameProjectAsync(projectName);
            await UpdateSubmodulesAsync();
            await FinalizeProjectAsync(projectName, outputDir);
        }
        finally
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
    }

    private async Task CloneTemplateRepositoryAsync()
    {
        _status.Status = "[bold yellow]Cloning template repository...[/]";

        var result = await RunProcessAsync("git", $"clone {_config.Template.GitRepoUrl} \"{_tempDir}\"");
        if (!result.Success)
        {
            throw new Exception($"Git clone failed: {result.Error}");
        }
    }

    private async Task RenameProjectAsync(string newName)
    {
        _status.Status = "[bold yellow]Renaming project files and contents...[/]";

        var oldName = _config.Template.TemplateName;
        var oldCoreName = oldName + ".Core";

        var allDirectories = Directory.GetDirectories(_tempDir, "*", SearchOption.AllDirectories)
                                    .OrderBy(d => d.Length)
                                    .ToList();

        foreach (var dir in allDirectories)
        {
            if (ShouldSkipPath(dir)) continue;

            string newDir = dir.Replace(oldName, newName);
            if (dir != newDir && !Directory.Exists(newDir))
            {
                Directory.Move(dir, newDir);
            }
        }

        var allFiles = Directory.GetFiles(_tempDir, "*.*", SearchOption.AllDirectories);
        foreach (var file in allFiles)
        {
            if (ShouldSkipPath(file)) continue;

            await RenameFileContentsAsync(file, oldName, newName);

            if (file.EndsWith(".sln.DotSettings"))
            {
                File.Delete(file);
            }
        }
    }

    private async Task UpdateSubmodulesAsync()
    {
        _status.Status = "[bold yellow]Updating submodules...[/]";

        var result = await RunProcessAsync("git", $"-C \"{_tempDir}\" submodule update --init --recursive");
        if (!result.Success)
        {
            throw new Exception($"Git submodule update failed: {result.Error}");
        }
    }

    private bool ShouldSkipPath(string path)
    {
        return path.Contains(".git") ||
               path.Contains("Migrations") ||
               path.Contains("tests") ||
               path.Contains("bin") ||
               path.Contains("obj") ||
               path.EndsWith(".Core") ||
               path.Contains($"{Path.DirectorySeparatorChar}.Core{Path.DirectorySeparatorChar}");
    }

    private async Task RenameFileContentsAsync(string file, string oldName, string newName)
    {
        var content = await File.ReadAllTextAsync(file);
        content = ReplaceContentExcludingCore(content, oldName, newName);

        if (file.EndsWith(".cs") || file.EndsWith(".cshtml"))
        {
            content = UpdateUsingStatements(content, oldName, newName);
        }

        if (file.EndsWith(".csproj"))
        {
            content = UpdateProjectReferences(content, oldName, newName);
        }

        await File.WriteAllTextAsync(file, content);

        string newFilePath = file.Replace(oldName, newName);
        if (file != newFilePath)
        {
            string newFileDirectory = Path.GetDirectoryName(newFilePath)!;
            Directory.CreateDirectory(newFileDirectory);

            if (!File.Exists(newFilePath))
            {
                File.Move(file, newFilePath);
            }
        }
    }

    private string ReplaceContentExcludingCore(string content, string oldName, string newName)
    {
        return Regex.Replace(content, $@"\b{Regex.Escape(oldName)}\b(?!\.Core)", newName);
    }

    private string UpdateUsingStatements(string content, string oldName, string newName)
    {
        return Regex.Replace(content, $@"using\s+{Regex.Escape(oldName)}\.(?!Core)", $"using {newName}.");
    }

    private string UpdateProjectReferences(string content, string oldName, string newName)
    {
        return Regex.Replace(content,
            $@"<ProjectReference\s+Include=""(.*?){Regex.Escape(oldName)}(?!\.Core)(.*?)""",
            m => $"<ProjectReference Include=\"{m.Groups[1].Value}{newName}{m.Groups[2].Value}\"");
    }

    private async Task FinalizeProjectAsync(string projectName, string outputDir)
    {
        _status.Status = "[bold green]Finalizing project...[/]";

        var finalPath = Path.Combine(outputDir, projectName);
        if (Directory.Exists(finalPath))
        {
            Directory.Delete(finalPath, true);
        }

        Directory.Move(_tempDir, finalPath);

        var tree = new Tree($"[green]Project Generated:[/] {projectName}")
            .Style(Style.Parse("cyan"));

        tree.AddNode($"[blue]Location:[/] [link={finalPath}]{finalPath}[/]");
        tree.AddNode($"[blue]Template:[/] {_config.Template.TemplateName}");

        AnsiConsole.Write(new Panel(tree)
            .Header("Success!")
            .BorderColor(Color.Green));

        AnsiConsole.MarkupLine("\n[grey]Click the path above to open the project location[/]");
    }

    private record ProcessResult(bool Success, string Output, string Error);

    private async Task<ProcessResult> RunProcessAsync(string command, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var output = new List<string>();
        var error = new List<string>();

        process.OutputDataReceived += (s, e) => { if (e.Data != null) output.Add(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return new ProcessResult(
            process.ExitCode == 0,
            string.Join(Environment.NewLine, output),
            string.Join(Environment.NewLine, error));
    }
}
