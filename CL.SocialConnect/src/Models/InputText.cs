using System.Globalization;

namespace CL.SocialConnect.Models;

/// <summary>Helpers for caller-supplied text that reaches logs, events or error messages.</summary>
internal static class InputText
{
    /// <summary>
    /// Makes caller input safe to log or publish: control characters (CR, LF, ...) and line separators become
    /// <c>?</c>, so a crafted parameter cannot forge log lines, and long input is cut to 64 characters.
    /// </summary>
    internal static string ForLog(string? value) => CL.Internal.LogText.Sanitize(value);
}
