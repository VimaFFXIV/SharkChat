using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SharkChat;

/// <summary>
/// Pure-static helper that applies substitution rules to an outgoing chat string.
/// </summary>
public static class Substitutor
{
    // FFXIV chat has a 500-byte payload limit; leave a small buffer.
    private const int MaxResultLength = 490;

    /// <summary>
    /// Apply all enabled rules to <paramref name="rawInput"/> and return the
    /// (possibly modified) string ready to be forwarded to the game.
    /// </summary>
    public static string Apply(string rawInput, List<SubstitutionRule> rules)
    {
        if (string.IsNullOrEmpty(rawInput) || rules.Count == 0)
            return rawInput;

        // Split a leading /command prefix from the actual message content.
        // e.g. "/say Hello thanks" → prefix="/say ", body="Hello thanks"
        // Substitutions are applied only to the body so the command itself is
        // never accidentally mangled.
        string prefix = string.Empty;
        string body   = rawInput;

        if (rawInput.StartsWith("/", StringComparison.Ordinal))
        {
            int spaceIdx = rawInput.IndexOf(' ');
            if (spaceIdx < 0)
                return rawInput; // bare command like "/sit" — nothing to replace

            prefix = rawInput[..(spaceIdx + 1)]; // "/say "
            body   = rawInput[(spaceIdx + 1)..]; // "Hello thanks"
        }

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.From))
                continue;

            body = ApplyRule(body, rule);
        }

        var result = prefix + body;

        // Guard against a substitution producing a message that's too long.
        return result.Length > MaxResultLength ? rawInput : result;
    }

    private static string ApplyRule(string text, SubstitutionRule rule)
    {
        var regexOptions = rule.CaseSensitive
            ? RegexOptions.None
            : RegexOptions.IgnoreCase;

        string pattern = rule.WholeWordOnly
            ? $@"\b{Regex.Escape(rule.From)}\b"
            : Regex.Escape(rule.From);

        try
        {
            return Regex.Replace(text, pattern, rule.To, regexOptions);
        }
        catch (RegexMatchTimeoutException)
        {
            return text; // never block the user's message on a regex timeout
        }
    }
}
