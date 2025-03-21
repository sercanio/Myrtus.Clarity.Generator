﻿using System.Reflection;
using System.Text.Json;
using Spectre.Console;
using Myrtus.Clarity.Generator.Common.Models;

namespace Myrtus.Clarity.Generator.DataAccess
{
    public class ConfigurationService
    {
        public async Task<AppSettings?> LoadConfigurationAsync()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "Myrtus.Clarity.Generator.DataAccess.appsettings.json"; // Ensure this matches the actual resource name

                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Embedded configuration file '{resourceName}' not found.");
                    return null;
                }

                using var reader = new StreamReader(stream);
                var configContent = await reader.ReadToEndAsync();
                var config = JsonSerializer.Deserialize<AppSettings>(configContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (string.IsNullOrEmpty(config?.Template?.GitRepoUrl))
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] Invalid configuration: Git repo URL is missing");
                    return null;
                }

                return config;
            }
            catch (Exception ex)
            {
                var safeMessage = Markup.Escape(ex.Message);
                AnsiConsole.MarkupLine($"[red]Configuration Error:[/] {safeMessage}");
                return null;
            }
        }
    }
}
