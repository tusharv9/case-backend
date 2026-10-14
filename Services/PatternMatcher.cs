namespace CaseManagement.Api.Services;

using System.Text.RegularExpressions;

/// <summary>
/// Matches administrator-written patterns the way the browser does. The form tests a pattern with JavaScript's engine, where
/// \d, \w and \s are ASCII-only; .NET's default also accepts other scripts' digits and letters, so a value could pass here that the
/// form refused. ECMAScript mode closes that gap. A pattern that uses .NET-only syntax (look-behind, Unicode categories…) cannot be
/// compiled in that mode, so it falls back to the default engine rather than being rejected.
/// </summary>
public static class PatternMatcher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public static bool IsMatch(string value, string pattern)
    {
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.ECMAScript, Timeout); }
        catch (ArgumentException) { regex = new Regex(pattern, RegexOptions.None, Timeout); }   // still throws for a truly invalid pattern
        return regex.IsMatch(value);
    }
}
