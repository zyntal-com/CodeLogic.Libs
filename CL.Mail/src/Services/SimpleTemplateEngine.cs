using CL.Mail.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CL.Mail.Services;

/// <summary>
/// Lightweight template engine supporting variable substitution, conditionals, loops,
/// named sections, and optional layout wrapping.
/// <para>
/// Supported syntax:
/// <list type="bullet">
///   <item><c>{{variable}}</c> — variable substitution</item>
///   <item><c>${variable}</c> — variable substitution (alternate syntax)</item>
///   <item><c>{variable}</c> — variable substitution (legacy syntax)</item>
///   <item><c>{{{variable}}}</c> — raw substitution: not HTML-encoded in an HTML body (trusted HTML only)</item>
///   <item><c>{{#if var}}…{{/if}}</c> — conditional block</item>
///   <item><c>{{#if var}}…{{#else}}…{{/if}}</c> — conditional with else</item>
///   <item><c>{{#each items}}…{{/each}}</c> — loop over a collection</item>
///   <item><c>{{#section name}}…{{/section}}</c> — named section for layout composition</item>
/// </list>
/// </para>
/// <para>
/// In <see cref="MailTemplate.HtmlBody"/> (and an HTML layout) every substituted value is
/// HTML-encoded, so user-supplied values cannot inject markup. Use <c>{{{variable}}}</c> for values
/// that are trusted HTML. Subjects and text bodies are never encoded. A substituted value is
/// inserted once: placeholders inside it are not expanded.
/// </para>
/// </summary>
public sealed partial class SimpleTemplateEngine : IMailTemplateEngine
{
    private readonly ILogger? _logger;
    private readonly IMailTemplateProvider? _templateProvider;

    /// <inheritdoc/>
    public string Name => "Simple";

    /// <summary>Initialises without layout support.</summary>
    /// <param name="logger">Optional logger.</param>
    public SimpleTemplateEngine(ILogger? logger = null) => _logger = logger;

    /// <summary>Initialises with layout support via a template provider.</summary>
    /// <param name="templateProvider">Provider used to load layout templates.</param>
    /// <param name="logger">Optional logger.</param>
    public SimpleTemplateEngine(IMailTemplateProvider templateProvider, ILogger? logger = null)
    {
        _templateProvider = templateProvider ?? throw new ArgumentNullException(nameof(templateProvider));
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Result<RenderedTemplate>> RenderAsync(
        MailTemplate template,
        Dictionary<string, object?> variables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        variables ??= [];

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subject = RenderText(template.Subject, variables, html: false);
            var textBody = template.TextBody is not null ? RenderText(template.TextBody, variables, html: false) : null;
            var htmlBody = template.HtmlBody is not null ? RenderText(template.HtmlBody, variables, html: true) : null;

            // Apply layout if specified and a provider is available
            if (!string.IsNullOrWhiteSpace(template.Layout) && _templateProvider is not null)
            {
                var layoutResult = await _templateProvider
                    .LoadTemplateAsync(template.Layout, cancellationToken)
                    .ConfigureAwait(false);

                if (layoutResult.IsSuccess && layoutResult.Value is not null)
                {
                    var layout = layoutResult.Value;

                    // The child's rendered sections (already encoded where needed) go into the
                    // layout as raw values: text sections into the text layout, HTML into the HTML one.
                    if (layout.TextBody is not null)
                        textBody = RenderLayout(layout.TextBody, textBody, variables, html: false);
                    if (layout.HtmlBody is not null)
                        htmlBody = RenderLayout(layout.HtmlBody, htmlBody, variables, html: true);
                }
                else
                {
                    _logger?.Warning($"Layout template '{template.Layout}' not found — rendering without layout");
                }
            }

            return new RenderedTemplate
            {
                Subject = subject,
                TextBody = textBody,
                HtmlBody = htmlBody
            };
        }
        catch (OperationCanceledException)
        {
            return MailErrors.Cancelled();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error rendering template '{template.Id}'", ex);
            return MailErrors.Internal(MailErrorCodes.TemplateRenderingFailed, ex);
        }
    }

    // ── Rendering pipeline ────────────────────────────────────────────────────

    private string RenderText(string text, Dictionary<string, object?> variables, bool html, ISet<string>? rawKeys = null)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var state = new RenderState(html, rawKeys);
        var result = ProcessConditionals(text, variables);
        result = ProcessLoops(result, variables, state);
        result = SubstituteVariables(result, variables, state);
        return state.Resolve(result);
    }

    private string RenderLayout(string layoutBody, string? childBody, Dictionary<string, object?> variables, bool html)
    {
        var sections = childBody is not null ? ExtractSections(childBody) : [];
        sections["body"] = StripSections(childBody ?? "");

        var layoutVars = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
            layoutVars[section.Key] = section.Value;
        return RenderText(layoutBody, layoutVars, html, new HashSet<string>(sections.Keys, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One render pass: substituted values are parked behind private-use markers and put back by
    /// <see cref="Resolve"/> at the end, so nothing inside a value is ever processed as template syntax.
    /// </summary>
    private sealed partial class RenderState(bool html, ISet<string>? rawKeys)
    {
        private const string Open = "\uE000";
        private const string Close = "\uE001";
        [GeneratedRegex("\uE000(\\d+)\uE001")] private static partial Regex Marker();
        private readonly List<string> _values = [];

        public string Insert(string key, object? value, bool raw)
        {
            var text = value?.ToString() ?? "";
            if (html && !raw && rawKeys?.Contains(key) != true)
                text = WebUtility.HtmlEncode(text);
            _values.Add(text);
            return Open + (_values.Count - 1).ToString(CultureInfo.InvariantCulture) + Close;
        }

        public string Resolve(string text) =>
            _values.Count == 0 ? text : Marker().Replace(text, m => _values[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
    }

    /// <summary>
    /// Processes <c>{{#if var}}…{{/if}}</c> blocks iteratively from innermost out.
    /// </summary>
    private static string ProcessConditionals(string text, Dictionary<string, object?> variables)
    {
        const int MaxIterations = 100;
        for (var i = 0; i < MaxIterations; i++)
        {
            var match = InnermostIf().Match(text);

            if (!match.Success) break;

            var varName = match.Groups[1].Value;
            var inner = match.Groups[2].Value;
            var truthy = IsTruthy(variables, varName);

            var elseIdx = inner.IndexOf("{{#else}}", StringComparison.Ordinal);
            var replacement = elseIdx >= 0
                ? (truthy ? inner[..elseIdx] : inner[(elseIdx + 9)..])
                : (truthy ? inner : "");

            text = string.Concat(text.AsSpan(0, match.Index), replacement, text.AsSpan(match.Index + match.Length));
        }
        return text;
    }

    /// <summary>
    /// Processes <c>{{#each items}}…{{/each}}</c> blocks.
    /// Items can be <c>Dictionary&lt;string, object?&gt;</c> or any type (via reflection).
    /// </summary>
    private string ProcessLoops(string text, Dictionary<string, object?> variables, RenderState state)
    {
        const int MaxIterations = 100;
        for (var i = 0; i < MaxIterations; i++)
        {
            var match = EachBlock().Match(text);

            if (!match.Success) break;

            var collectionName = match.Groups[1].Value;
            var itemTemplate = match.Groups[2].Value;
            var sb = new StringBuilder();

            if (TryGetVariable(variables, collectionName, out var raw) && raw is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    var iterVars = new Dictionary<string, object?>(variables, StringComparer.OrdinalIgnoreCase);

                    if (item is Dictionary<string, object?> d)
                        foreach (var kvp in d) iterVars[kvp.Key] = kvp.Value;
                    else if (item is IDictionary<string, object> d2)
                        foreach (var kvp in d2) iterVars[kvp.Key] = kvp.Value;
                    else if (item is not null)
                        foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { iterVars[prop.Name] = prop.GetValue(item); }
                            catch (Exception ex) { _logger?.Debug($"Skip inaccessible property '{prop.Name}': {ex.Message}"); }
                        }

                    var rendered = ProcessConditionals(itemTemplate, iterVars);
                    rendered = ProcessLoops(rendered, iterVars, state);
                    rendered = SubstituteVariables(rendered, iterVars, state);
                    sb.Append(rendered);
                }
            }

            text = string.Concat(text.AsSpan(0, match.Index), sb.ToString(), text.AsSpan(match.Index + match.Length));
        }
        return text;
    }

    /// <summary>
    /// Replaces <c>{{{var}}}</c>, <c>{{var}}</c>, <c>${var}</c> and <c>{var}</c> placeholders in one pass.
    /// Unknown names are left as they are.
    /// </summary>
    private static string SubstituteVariables(string text, Dictionary<string, object?> variables, RenderState state)
    {
        if (string.IsNullOrEmpty(text)) return text;

        return Placeholder().Replace(text, m =>
        {
            var raw = m.Groups["raw"].Success;
            var key = raw ? m.Groups["raw"].Value
                : m.Groups["double"].Success ? m.Groups["double"].Value
                : m.Groups["dollar"].Success ? m.Groups["dollar"].Value
                : m.Groups["single"].Value;
            return TryGetVariable(variables, key, out var value) ? state.Insert(key, value, raw) : m.Value;
        });
    }

    // Source-generated regexes: parsed and compiled at build time, not on each render.
    [GeneratedRegex(@"\{\{\{(?<raw>\w+)\}\}\}|\{\{(?<double>\w+)\}\}|\$\{(?<dollar>\w+)\}|(?<!\{)\{(?<single>\w+)\}(?!\})")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{\{#if\s+(\w+)\}\}((?:(?!\{\{#if\b).)*?)\{\{/if\}\}", RegexOptions.Singleline)]
    private static partial Regex InnermostIf();

    [GeneratedRegex(@"\{\{#each\s+(\w+)\}\}(.*?)\{\{/each\}\}", RegexOptions.Singleline)]
    private static partial Regex EachBlock();

    [GeneratedRegex(@"\{\{#section\s+(\w+)\}\}(.*?)\{\{/section\}\}", RegexOptions.Singleline)]
    private static partial Regex SectionBlock();

    [GeneratedRegex(@"\{\{#section\s+\w+\}\}.*?\{\{/section\}\}", RegexOptions.Singleline)]
    private static partial Regex SectionBlockUnnamed();

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static bool IsTruthy(Dictionary<string, object?> variables, string varName)
    {
        if (!TryGetVariable(variables, varName, out var value)) return false;
        return value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            string s => !string.IsNullOrEmpty(s)
                        && !s.Equals("false", StringComparison.OrdinalIgnoreCase)
                        && s != "0",
            _ => true
        };
    }

    private static bool TryGetVariable(Dictionary<string, object?> variables, string key, out object? value)
    {
        if (variables.TryGetValue(key, out value)) return true;
        foreach (var kvp in variables)
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        value = null;
        return false;
    }

    private static Dictionary<string, string> ExtractSections(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in SectionBlock().Matches(text))
            sections[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return sections;
    }

    private static string StripSections(string text) =>
        SectionBlockUnnamed().Replace(text, "").Trim();
}
