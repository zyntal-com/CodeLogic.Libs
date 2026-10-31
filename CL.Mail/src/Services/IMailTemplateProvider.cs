using CL.Mail.Models;
using CodeLogic.Core.Results;

namespace CL.Mail.Services;

/// <summary>
/// Loads <see cref="MailTemplate"/> objects from a backing store (file system, database, etc.).
/// </summary>
public interface IMailTemplateProvider
{
    /// <summary>
    /// Loads a single template by its <see cref="MailTemplate.Id"/>.
    /// </summary>
    /// <param name="templateId">The template identifier to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The template, or a <see cref="NotFoundError"/> with code
    /// <see cref="MailErrorCodes.TemplateNotFound"/> if no matching template exists.
    /// </returns>
    Task<Result<MailTemplate>> LoadTemplateAsync(string templateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all available template IDs from the backing store.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<IReadOnlyList<string>>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a template to the backing store, creating it if it does not exist.
    /// </summary>
    /// <param name="template">The template to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> SaveTemplateAsync(MailTemplate template, CancellationToken cancellationToken = default);
}
