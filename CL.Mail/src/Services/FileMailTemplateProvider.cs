using CL.Mail.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using System.Text.Json;

namespace CL.Mail.Services;

/// <summary>
/// Loads and saves <see cref="MailTemplate"/> objects as JSON files on the local file system.
/// Each template is stored in its own <c>{templateId}.json</c> file inside
/// the configured template directory.
/// </summary>
public sealed class FileMailTemplateProvider : IMailTemplateProvider
{
    private readonly string _directory;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initialises the provider pointing at <paramref name="directory"/>.
    /// The directory is created automatically if it does not exist.
    /// </summary>
    /// <param name="directory">Absolute path to the template directory.</param>
    /// <param name="logger">Optional logger.</param>
    public FileMailTemplateProvider(string directory, ILogger? logger = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc/>
    public async Task<Result<MailTemplate>> LoadTemplateAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        var path = TemplatePath(templateId);

        if (!File.Exists(path))
        {
            _logger?.Debug($"Template not found: {path}");
            return MailErrors.NotFound(MailErrorCodes.TemplateNotFound, $"Template '{templateId}' not found");
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var template = JsonSerializer.Deserialize<MailTemplate>(json, _jsonOptions);

            if (template is null)
                return MailErrors.Internal(MailErrorCodes.TemplateRenderingFailed, $"Template '{templateId}' could not be deserialized");

            return template;
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error loading template '{templateId}'", ex);
            return MailErrors.Internal(MailErrorCodes.TemplateRenderingFailed, ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result<IReadOnlyList<string>>> ListTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ids = Directory
                .EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(Result<IReadOnlyList<string>>.Success(ids.AsReadOnly()));
        }
        catch (Exception ex)
        {
            _logger?.Error("Error listing templates", ex);
            return Task.FromResult(Result<IReadOnlyList<string>>.Failure(MailErrors.Internal(MailErrorCodes.Unknown, ex)));
        }
    }

    /// <inheritdoc/>
    public async Task<Result> SaveTemplateAsync(
        MailTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        try
        {
            var path = TemplatePath(template.Id);
            var json = JsonSerializer.Serialize(template, _jsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);

            _logger?.Info($"Saved template: {template.Id}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error saving template '{template.Id}'", ex);
            return MailErrors.Internal(MailErrorCodes.Unknown, ex);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string TemplatePath(string id) =>
        Path.Combine(_directory, $"{id}.json");
}
