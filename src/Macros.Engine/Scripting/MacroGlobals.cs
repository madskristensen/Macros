using System;
using Macros.Engine.Triggers;
using EnvDTE80;
using Microsoft.VisualStudio.Threading;

namespace Macros.Engine.Scripting;

/// <summary>
/// The <c>Globals</c> object passed to <c>CSharpScript.RunAsync</c>. Every public top-level
/// statement in a user-authored <c>.csx</c> macro can reference these properties as if they were
/// in scope (<c>DTE.ActiveDocument</c>, <c>Context.MacroName</c>, etc.).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no <c>VS</c> property?</b> The Community Toolkit's <c>Community.VisualStudio.Toolkit.VS</c>
/// is a <see langword="static"/> class — it cannot be exposed as an instance property. Rather than
/// wrap every member in a thin instance façade, the codegen (see <c>m2-csharp-codegen</c>) emits
/// <c>using static Community.VisualStudio.Toolkit.VS;</c> at the top of every generated <c>.csx</c>.
/// User scripts therefore call <c>Solutions.GetCurrentSolutionAsync()</c> (etc.) directly, with no
/// receiver, exactly as if <c>VS</c> were imported.
/// </para>
/// <para>
/// The class is <see langword="sealed"/> so Roslyn can compile a single closed shape against it;
/// changing the surface is a contract break for every previously-recorded macro and is gated by
/// the macro engine versioning policy.
/// </para>
/// </remarks>
public sealed class MacroGlobals
{
    /// <summary>
    /// Initializes a new instance bound to the supplied DTE automation root and macro context.
    /// </summary>
    /// <param name="dte">The hosting Visual Studio's <see cref="DTE2"/> automation root.</param>
    /// <param name="context">Per-invocation context (macro name, trigger info, cancellation).</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dte"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public MacroGlobals(DTE2 dte, IMacroContext context)
    {
        DTE = dte ?? throw new ArgumentNullException(nameof(dte));
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Gets the Visual Studio <see cref="DTE2"/> automation root. The full classic automation
    /// surface (<c>ActiveDocument</c>, <c>Solution</c>, <c>ExecuteCommand</c>, …) is reachable
    /// from here. Most calls require the UI thread; helper methods in
    /// <see cref="Helpers"/> handle the marshalling on the script's behalf.
    /// </summary>
    public DTE2 DTE { get; }

    /// <summary>Gets the per-invocation <see cref="IMacroContext"/> describing this playback.</summary>
    public IMacroContext Context { get; }

    /// <summary>
    /// Gets the typed trigger that initiated this playback. Shortcut for <c>Context.Trigger</c>
    /// so script authors can write <c>Trigger.Kind</c> / <c>Trigger.CommandName</c> directly.
    /// </summary>
    public IMacroTrigger Trigger => Context.Trigger;

    internal JoinableTaskFactory? UiThreadFactory { get; set; }

    internal IMacroPromptService? PromptService { get; set; }
}
