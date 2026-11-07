using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Include and exclude globs over relative paths: <c>**</c> spans folders, <c>*</c> and <c>?</c> stay in one name.</summary>
internal sealed class PathFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    public PathFilter(StorageCompareOptions options)
    {
        _include = [.. options.Include.Select(Compile)];
        _exclude = [.. options.Exclude.Select(Compile)];
    }

    /// <summary>Whether a path is left out; a folder matching an exclude pattern is left out with its contents.</summary>
    public bool Excludes(string relativePath, bool isDirectory)
    {
        if (_exclude.Any(glob => glob.IsMatch(relativePath) || (isDirectory && glob.IsMatch(relativePath + "/"))))
            return true;
        return !isDirectory && _include.Length > 0 && !_include.Any(glob => glob.IsMatch(relativePath));
    }

    internal static Regex Compile(string glob)
    {
        var pattern = new StringBuilder("^");
        var text = glob.Replace('\\', '/').TrimStart('/');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var slashAfter = i + 2 < text.Length && text[i + 2] == '/';
                pattern.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') pattern.Append("[^/]*");
            else if (c == '?') pattern.Append("[^/]");
            else pattern.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }
}
