namespace Macros;

/// <summary>
/// Hand-authored mirror of the IDSymbol values in VSCommandTable.vsct.
/// Single File Generator (VsctGenerator) only runs in the VS IDE, not during
/// dotnet build, so we maintain this file by hand. If you add or change an
/// IDSymbol in the .vsct, update this file in the same commit.
/// </summary>
internal static class PackageIds
{
    // Menu/Group IDs
    public const int MacrosToolbar = 0x1000;
    public const int MacrosToolbarGroup = 0x1010;
    public const int MacrosToolWindowToolbar = 0x1020;
    public const int MacrosToolWindowToolbarGroup = 0x1030;
    public const int MacrosContextMenu = 0x1040;
    public const int MacrosContextMenuGroup = 0x1050;

    // Command IDs
    public const int cmdidMacrosRecord = 0x0100;
    public const int cmdidMacrosStop = 0x0101;
    public const int cmdidMacrosPlayLast = 0x0102;
    public const int cmdidMacrosPlayNamed = 0x0103;
    public const int cmdidMacrosSaveAs = 0x0104;
    public const int cmdidMacrosShowWindow = 0x0105;
    public const int cmdidMacrosDelete = 0x0106;
    public const int cmdidMacrosRename = 0x0107;
    public const int cmdidMacrosEdit = 0x0108;
    public const int cmdidMacrosManageTriggers = 0x0109;
    public const int cmdidMacrosNewMacro = 0x010A;
    public const int cmdidMacrosRefresh = 0x010B;
    public const int cmdidMacrosMoveToRepo = 0x010C;
    public const int cmdidMacrosMoveToGlobal = 0x010D;
    public const int cmdidMacrosToggleTriggers = 0x0014;

    // Tool window context menu (m3-context-menu). Mirrors the 0x2xxx block at the bottom of
    // VSCommandTable.vsct's GuidSymbol section. IVsUIShell.ShowContextMenu requires the
    // menu's cmdid as a uint, hence the wider type here.
    public const int MacrosToolWindowContextMenu = 0x2000;
    public const int MacrosContextMenuGroup1 = 0x2100;
    public const int MacrosContextMenuGroup2 = 0x2200;
    public const int cmdidMacrosCtxPlay = 0x2110;
    public const int cmdidMacrosCtxEdit = 0x2111;
    public const int cmdidMacrosCtxRename = 0x2112;
    public const int cmdidMacrosCtxDelete = 0x2113;
    public const int cmdidMacrosCtxMoveToRepo = 0x2114;
    public const int cmdidMacrosCtxMoveToGlobal = 0x2115;
    public const int cmdidMacrosCtxOpenFolder = 0x2210;
}
