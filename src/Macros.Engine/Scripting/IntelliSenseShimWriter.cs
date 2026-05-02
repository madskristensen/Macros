using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace Macros.Engine.Scripting;

/// <summary>
/// Atomic, idempotent writer that generates and persists the IntelliSense shim
/// (<c>.intellisense/Macros.Intellisense.csx</c>) under a macro store root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic write.</b> The new content is written to a uniquely-named sibling <c>.tmp</c>
/// file first, then swapped into place via <see cref="File.Replace(string,string,string)"/>
/// when an existing file is being overwritten, or <see cref="File.Move(string,string)"/> when
/// the destination does not yet exist. A crash mid-write therefore leaves the previous file
/// intact and at worst leaves a stray <c>.tmp</c> file behind (cleaned up on the next write).
/// </para>
/// <para>
/// <b>Idempotent.</b> Before writing, the writer computes a SHA-256 hash of the new content
/// and compares it to the SHA-256 of the existing file. When the hashes match the file is
/// left untouched and <see cref="IntelliSenseShimWriteResult.Wrote"/> is
/// <see langword="false"/>. This prevents spurious file-watcher noise when the VSIX reloads
/// but the set of assemblies hasn't changed.
/// </para>
/// <para>
/// <b>Thread-safety.</b> The class holds no shared mutable state; all operations are local
/// path computations and standard file-system calls. Concurrent writes to the same destination
/// are safe: <see cref="File.Replace"/> is atomic at the OS level on Windows (uses
/// <c>MoveFileEx MOVEFILE_REPLACE_EXISTING</c>), so the destination is always in a valid
/// state after any single write completes.
/// </para>
/// </remarks>
public static class IntelliSenseShimWriter
{
    /// <summary>Name of the subfolder (under the store root) that holds the shim.</summary>
    public const string ShimFolderName = ".intellisense";

    /// <summary>File name of the generated IntelliSense shim script.</summary>
    public const string ShimFileName = "Macros.Intellisense.csx";

    // UTF-8 without BOM: the shim is machine-generated and consumed by Roslyn's script
    // parser, which is BOM-agnostic. No-BOM keeps the file lean and avoids any legacy
    // editors that stumble on the BOM.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the IntelliSense shim to
    /// <c>&lt;storeRoot&gt;/.intellisense/Macros.Intellisense.csx</c>.
    /// </summary>
    /// <param name="storeRoot">
    /// Absolute path to the macro store root (e.g. <c>%APPDATA%\Macros</c> or
    /// <c>&lt;solutionDir&gt;\.vs\Macros</c>). Must not be <see langword="null"/>, empty,
    /// or whitespace.
    /// </param>
    /// <returns>
    /// A <see cref="IntelliSenseShimWriteResult"/> with <see cref="IntelliSenseShimWriteResult.Wrote"/>
    /// <see langword="true"/> when the file was created or updated, <see langword="false"/>
    /// when the existing content was already current (no-op).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="storeRoot"/> is <see langword="null"/>, empty, or whitespace.
    /// </exception>
    public static IntelliSenseShimWriteResult Write(string storeRoot)
    {
        if (string.IsNullOrWhiteSpace(storeRoot))
        {
            throw new ArgumentException("Store root must be non-empty.", nameof(storeRoot));
        }

        IReadOnlyList<string> resolvedPaths = ResolveAssemblyPaths();
        string shimSource = IntelliSenseShim.Generate(resolvedPaths);
        byte[] newBytes = Utf8NoBom.GetBytes(shimSource);
        byte[] newHash = ComputeSha256(newBytes);

        string shimFolder = Path.Combine(storeRoot, ShimFolderName);
        string shimPath = Path.Combine(shimFolder, ShimFileName);

        // No-op guard: skip the write if the existing file already has the same content.
        if (File.Exists(shimPath))
        {
            try
            {
                byte[] existingBytes = File.ReadAllBytes(shimPath);
                if (HashesEqual(ComputeSha256(existingBytes), newHash))
                {
                    return new IntelliSenseShimWriteResult(shimPath, Wrote: false, resolvedPaths);
                }
            }
            catch (IOException)
            {
                // Unreadable file (locked, corrupted) → fall through to overwrite.
            }
        }

        Directory.CreateDirectory(shimFolder);

        string tmpPath = shimPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, newBytes);
            SwapIntoPlace(tmpPath, shimPath);
        }
        catch
        {
            TryDeleteSilently(tmpPath);
            throw;
        }

        return new IntelliSenseShimWriteResult(shimPath, Wrote: true, resolvedPaths);
    }

    // ── private helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Discovers the absolute disk paths of the assemblies the generated macro scripts
    /// reference. Each assembly is located via <c>typeof(KnownType).Assembly.Location</c>.
    /// Entries whose <c>Location</c> is empty (assembly loaded from a byte array, uncommon
    /// in a hosted VS process) are skipped with a diagnostic trace rather than causing a
    /// crash. Duplicate paths (e.g. EnvDTE and EnvDTE80 resolving to the same Interop dll
    /// in VS18 Preview) are collapsed to a single entry — first occurrence wins.
    /// </summary>
    private static IReadOnlyList<string> ResolveAssemblyPaths()
    {
        var raw = new List<string>(5);
        TryAdd(raw, "EnvDTE", typeof(DTE).Assembly.Location);
        TryAdd(raw, "EnvDTE80", typeof(DTE2).Assembly.Location);
        TryAdd(raw, "Microsoft.VisualStudio.Shell.15.0", typeof(Package).Assembly.Location);
        TryAdd(raw, "Community.VisualStudio.Toolkit", typeof(VS).Assembly.Location);
        TryAdd(raw, "Macros.Engine", typeof(MacroGlobals).Assembly.Location);
        return Deduplicate(raw);
    }

    /// <summary>
    /// Returns a deduplicated, order-preserving list from <paramref name="paths"/>.
    /// Null/empty entries are dropped. Comparison is case-insensitive (Windows paths).
    /// First occurrence of any given path wins.
    /// </summary>
    internal static IReadOnlyList<string> Deduplicate(IEnumerable<string> paths)
    {
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (seen.Add(path))
                unique.Add(path);
            else
                Debug.WriteLine($"[IntelliSenseShimWriter] Duplicate assembly path '{path}'; skipping redundant #r entry.");
        }
        return unique;
    }

    private static void TryAdd(List<string> paths, string assemblyName, string? location)
    {
        if (string.IsNullOrEmpty(location))
        {
            Debug.WriteLine(
                $"[IntelliSenseShimWriter] Could not resolve location for '{assemblyName}' " +
                "(assembly may have been loaded from a byte array); skipping #r entry.");
            return;
        }

        paths.Add(location!);
    }

    private static void SwapIntoPlace(string tmpPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(tmpPath, destinationPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmpPath, destinationPath);
        }
    }

    private static void TryDeleteSilently(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup — don't mask the original failure.
        }
    }

    private static byte[] ComputeSha256(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    private static bool HashesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }
}

/// <summary>
/// Result returned by <see cref="IntelliSenseShimWriter.Write"/>.
/// </summary>
/// <param name="ShimPath">
/// Absolute path to the shim file on disk (whether or not it was written during this call).
/// </param>
/// <param name="Wrote">
/// <see langword="true"/> if the file was created or updated during this call;
/// <see langword="false"/> if the existing content was already current (no-op).
/// </param>
/// <param name="ResolvedAssemblyPaths">
/// The ordered list of absolute assembly paths that were discovered and passed to
/// <see cref="IntelliSenseShim.Generate"/>. May be shorter than the full set when
/// some assemblies report an empty <c>Location</c>.
/// </param>
public sealed record IntelliSenseShimWriteResult(
    string ShimPath,
    bool Wrote,
    IReadOnlyList<string> ResolvedAssemblyPaths);
