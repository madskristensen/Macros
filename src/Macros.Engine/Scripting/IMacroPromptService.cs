using System.Threading.Tasks;

namespace Macros.Engine.Scripting;

/// <summary>
/// UI abstraction for runtime macro prompts. Implemented by the VSIX layer because the engine
/// project does not own WPF dialog types.
/// </summary>
internal interface IMacroPromptService
{
    /// <summary>
    /// Shows a prompt and returns either the accepted input or the supplied default when the
    /// user cancels.
    /// </summary>
    Task<string> PromptAsync(string label, string defaultValue);
}
