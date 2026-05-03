namespace Macros.Engine;

/// <summary>
/// High-level lifecycle state of the macro engine.
/// </summary>
/// <remarks>
/// The engine is a single-instance state machine. Valid transitions are:
/// <list type="bullet">
///   <item><description><see cref="Idle"/> → <see cref="Recording"/> via <c>StartRecordingAsync</c></description></item>
///   <item><description><see cref="Recording"/> → <see cref="Idle"/> via <c>StopRecordingAsync</c> or <c>CancelAsync</c></description></item>
///   <item><description><see cref="Idle"/> → <see cref="Playing"/> via <c>PlayCurrentAsync</c> / <c>PlayByNameAsync</c></description></item>
///   <item><description><see cref="Playing"/> → <see cref="Idle"/> when playback completes or is cancelled</description></item>
/// </list>
/// Any other transition is invalid and the engine will throw <see cref="System.InvalidOperationException"/>.
/// </remarks>
public enum MacroState
{
    /// <summary>The engine is not recording or playing back; ready to start either operation.</summary>
    Idle,

    /// <summary>The engine is actively capturing user input into the active recording session.</summary>
    Recording,

    /// <summary>The engine is currently replaying a previously recorded macro.</summary>
    Playing,
}
