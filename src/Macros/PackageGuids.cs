namespace Macros;

/// <summary>
/// Package- and command-set GUIDs shared between C# code and the .vsct command table.
/// The values here MUST match the corresponding GUID symbols declared in VSCommandTable.vsct
/// (added in m1-vsct-symbols).
/// </summary>
internal static class PackageGuids
{
    /// <summary>
    /// The package GUID. Referenced by <see cref="MacrosPackage"/>'s <c>[Guid]</c> attribute and
    /// by the VsPackage asset in <c>source.extension.vsixmanifest</c>.
    /// </summary>
    public const string PackageGuidString = "d13e532a-bd43-40df-ae9f-8d05e54138c7";

    /// <summary>
    /// The command set GUID. All commands declared in VSCommandTable.vsct live under this set.
    /// </summary>
    public const string CommandSetGuidString = "1f6e8c4b-2d54-4a76-9c2c-9d8b5a7e1d31";

    /// <summary>
    /// UIContext GUID activated while the recorder is running. Used to drive
    /// VisibilityConstraints in the .vsct (e.g., show/hide Stop vs. Record).
    /// </summary>
    public const string RecordingContextGuidString = "f0a5d2c7-7e7c-4a47-bc6f-2c0a5b6f2c80";

    /// <summary>
    /// Inverse UIContext GUID activated when the recorder is NOT running. Drives the Record
    /// button's <c>VisibilityItem</c> in the .vsct so it hides while a recording is in progress.
    /// </summary>
    public const string NotRecordingContextGuidString = "c8e5a3b1-9f4d-4d87-b5a2-7e1f3a8c2d40";
}
