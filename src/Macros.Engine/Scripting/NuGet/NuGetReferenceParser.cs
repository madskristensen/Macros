using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Macros.Engine.Scripting.NuGet;

/// <summary>
/// Pure helper that extracts <c>#r "nuget: PackageId, Version"</c> directives from a
/// <c>.csx</c> script source. Mirrors the dotnet-script / C# Interactive convention so
/// existing recipes copy-paste cleanly.
/// </summary>
/// <remarks>
/// <para>
/// Recognised forms (all whitespace within the directive is tolerant):
/// </para>
/// <code>
/// #r "nuget: Newtonsoft.Json, 13.0.3"
/// #r "nuget:Newtonsoft.Json,13.0.3"
/// #r "nuget : Newtonsoft.Json , 13.0.3 "
/// </code>
/// <para>
/// The parser is permissive about whitespace and case-insensitive on the <c>nuget:</c>
/// scheme. It does <em>not</em> validate semantic version syntax — that responsibility
/// belongs to the resolver, which lets NuGet itself reject bad versions with a clearer
/// error than a parser regex could produce.
/// </para>
/// </remarks>
internal static class NuGetReferenceParser
{
    // The regex deliberately allows trailing whitespace inside the quotes, swallows leading
    // and trailing whitespace within the directive segments, and permits both `nuget:` and
    // `nuget :`. The script source is line-oriented so we anchor with `^` + `RegexOptions.Multiline`.
    private static readonly Regex DirectivePattern = new(
        @"^\s*#r\s+""\s*nuget\s*:\s*(?<id>[^,\s""][^,""]*?)\s*,\s*(?<version>[^""\s][^""]*?)\s*""\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Returns every NuGet reference in <paramref name="source"/>, in the order they
    /// appear. Duplicates are preserved — the resolver decides whether to deduplicate.
    /// </summary>
    /// <param name="source">The full <c>.csx</c> source text. <see langword="null"/> is treated as empty.</param>
    /// <returns>An ordered list of <see cref="NuGetReference"/> entries, possibly empty.</returns>
    public static IReadOnlyList<NuGetReference> Extract(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return Array.Empty<NuGetReference>();
        }

        var results = new List<NuGetReference>();
        foreach (Match match in DirectivePattern.Matches(source!))
        {
            string id = match.Groups["id"].Value.Trim();
            string version = match.Groups["version"].Value.Trim();
            if (id.Length == 0 || version.Length == 0)
            {
                continue;
            }

            results.Add(new NuGetReference(id, version));
        }

        return results;
    }

    /// <summary>
    /// Tests whether <paramref name="reference"/> — the string Roslyn passes to a
    /// <c>MetadataReferenceResolver.ResolveReference</c> implementation — looks like a
    /// NuGet directive (<c>"nuget: PackageId, Version"</c> with the leading <c>#r "</c>
    /// already stripped). Returns the parsed <see cref="NuGetReference"/> on success.
    /// </summary>
    /// <param name="reference">The reference text Roslyn provided. <see langword="null"/> returns false.</param>
    /// <param name="parsed">The parsed package id + version when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the reference is a NuGet directive, otherwise <see langword="false"/>.</returns>
    public static bool TryParseRoslynReference(string? reference, out NuGetReference parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        // Strip a leading "#r" (Roslyn typically passes only the inner quoted text but be
        // defensive for callers that hand us the whole directive line).
        string trimmed = reference!.Trim();
        if (trimmed.StartsWith("#r", StringComparison.OrdinalIgnoreCase))
        {
            int firstQuote = trimmed.IndexOf('"');
            int lastQuote = trimmed.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
            {
                trimmed = trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Trim();
            }
        }

        if (!trimmed.StartsWith("nuget", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        string body = trimmed.Substring(colon + 1).Trim();
        int comma = body.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        string id = body.Substring(0, comma).Trim();
        string version = body.Substring(comma + 1).Trim();
        if (id.Length == 0 || version.Length == 0)
        {
            return false;
        }

        parsed = new NuGetReference(id, version);
        return true;
    }

    /// <summary>
    /// Returns a stable cache key for a given set of references. Order-insensitive,
    /// case-insensitive on the package id, case-sensitive on the version (NuGet itself
    /// treats versions case-sensitively).
    /// </summary>
    /// <param name="refs">The package list to hash. <see langword="null"/> is treated as empty.</param>
    /// <returns>A 16-character hex digest derived from a SHA-256 of the canonical form.</returns>
    public static string ComputeCacheKey(IReadOnlyList<NuGetReference>? refs)
    {
        if (refs is null || refs.Count == 0)
        {
            return "empty";
        }

        var sorted = new List<string>(refs.Count);
        foreach (var r in refs)
        {
            sorted.Add($"{r.PackageId.ToLowerInvariant()}:{r.Version}");
        }

        sorted.Sort(StringComparer.Ordinal);
        string canonical = string.Join("|", sorted);

        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));

        var hex = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8 && i < bytes.Length; i++)
        {
            hex.Append(bytes[i].ToString("x2"));
        }

        return hex.ToString();
    }

    private static readonly char[] DisallowedInPath = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Validates that <paramref name="reference"/>'s package id and version are safe to
    /// embed in a file path. Used by the resolver to reject malicious inputs before
    /// generating a temp project.
    /// </summary>
    /// <param name="reference">The reference to validate.</param>
    /// <returns><see langword="true"/> when both id and version are filesystem-safe.</returns>
    public static bool IsPathSafe(NuGetReference reference)
        => IsPathSafe(reference.PackageId) && IsPathSafe(reference.Version);

    private static bool IsPathSafe(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (char c in value)
        {
            if (Array.IndexOf(DisallowedInPath, c) >= 0)
            {
                return false;
            }

            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A single <c>#r "nuget: ..."</c> directive's parsed payload. Immutable, value-equal,
/// safe to use as a dictionary key.
/// </summary>
/// <param name="PackageId">The NuGet package identifier (e.g. <c>"Newtonsoft.Json"</c>).</param>
/// <param name="Version">The exact version string (e.g. <c>"13.0.3"</c>) — no SemVer ranges.</param>
internal readonly record struct NuGetReference(string PackageId, string Version);
