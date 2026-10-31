using CL.Mail.Models;
using CodeLogic.Core.Results;

namespace CL.Mail.Services;

/// <summary>
/// Renders a <see cref="MailTemplate"/> by substituting variables, processing
/// conditionals and loops, and applying optional layout templates.
/// </summary>
public interface IMailTemplateEngine
{
    /// <summary>Display name of this engine (e.g., "Simple", "Razor").</summary>
    string Name { get; }

    /// <summary>
    /// Renders the template and returns a fully substituted <see cref="RenderedTemplate"/>.
    /// </summary>
    /// <returns>
    /// The rendered template, or an <see cref="Error"/> with code
    /// <see cref="MailErrorCodes.TemplateRenderingFailed"/> (or <see cref="MailErrorCodes.Cancelled"/>).
    /// </returns>
    /// <param name="template">The template to render.</param>
    /// <param name="variables">Key/value pairs injected into the template.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<RenderedTemplate>> RenderAsync(
        MailTemplate template,
        Dictionary<string, object?> variables,
        CancellationToken cancellationToken = default);
}
