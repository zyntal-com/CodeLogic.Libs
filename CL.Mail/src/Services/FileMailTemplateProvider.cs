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
/// <remarks>
/// Template IDs are file names, not paths: they may contain only ASCII letters, digits, <c>-</c>,
/// <c>_</c> and <c>.</c>, must start with a letter or digit, and are at most 128 characters long.
/// Any other ID fails with <see cref="MailErrorCodes.InvalidTemplateId"/>, so an externally supplied
/// ID can never read or write a file outside the template directory.
/// </remarks>
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
        ArgumentNullException.ThrowIfNull(directory);
        _directory = Path.GetFullPath(directory);
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc/>
    public async Task<Result<MailTemplate>> LoadTemplateAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetTemplatePath(templateId, out var path, out var invalid))
            return invalid;

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

        if (!TryGetTemplatePath(template.Id, out var path, out var invalid))
            return invalid;

        try
        {
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

    /// <summary>Longest accepted template ID.</summary>
    public const int MaxTemplateIdLength = 128;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="templateId"/> is a valid file-name-safe
    /// template ID (ASCII letters, digits, <c>-</c>, <c>_</c>, <c>.</c>; starts with a letter or digit;
    /// at most <see cref="MaxTemplateIdLength"/> characters).
    /// </summary>
    public static bool IsValidTemplateId(string? templateId)
    {
        if (string.IsNullOrEmpty(templateId) || templateId.Length > MaxTemplateIdLength)
            return false;
        if (!char.IsAsciiLetterOrDigit(templateId[0]))
            return false;
        foreach (var c in templateId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                return false;
        }
        return true;
    }

    private bool TryGetTemplatePath(string? id, out string path, out Error error)
    {
        path = string.Empty;
        error = null!;

        if (!IsValidTemplateId(id))
        {
            error = MailErrors.Validation(MailErrorCodes.InvalidTemplateId,
                "Template IDs may contain only ASCII letters, digits, '-', '_' and '.', must start with a letter or digit, " +
                $"and be at most {MaxTemplateIdLength} characters");
            return false;
        }

        // Defence in depth: the ID format above already rules out separators and rooted paths,
        // but containment is re-checked on the normalized path.
        var full = Path.GetFullPath(Path.Combine(_directory, id + ".json"));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (Path.GetDirectoryName(full) is not { } parent
            || !string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(_directory), comparison))
        {
            error = MailErrors.Validation(MailErrorCodes.InvalidTemplateId, "Template ID resolves outside the template directory");
            return false;
        }

        path = full;
        return true;
    }
}
