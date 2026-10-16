using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TCV.App.Workspaces;

namespace TCV.App.Views.Dialogs;

/// <summary>
/// The standard Windows colour dialog (<c>ChooseColor</c>), called directly: WPF has no colour
/// picker, and pulling in Windows Forms for one dialog is not worth the dependency.
/// </summary>
internal static class SystemColorPicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ChooseColorInfo
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public int Result;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
    }

    private const int CcRgbInit = 0x1;
    private const int CcFullOpen = 0x2;
    private const int CcAnyColor = 0x100;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChooseColorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref ChooseColorInfo info);

    /// <summary>
    /// The dialog's "custom colours" row, kept for the session. It starts with the ready-made
    /// swatches, so the palette the user already knows is there in the big dialog too.
    /// </summary>
    private static readonly int[] CustomColors = WorkspaceColor.Presets
        .Take(16)
        .Select(ToColorRef)
        .Concat(Enumerable.Repeat(0xFFFFFF, 16))
        .Take(16)
        .ToArray();

    /// <summary>Shows the dialog starting on <paramref name="initialHex"/>; the chosen colour as
    /// <c>#RRGGBB</c>, or null when it was cancelled.</summary>
    public static string? Pick(Window owner, string? initialHex)
    {
        var info = new ChooseColorInfo
        {
            StructSize = Marshal.SizeOf<ChooseColorInfo>(),
            Owner = new WindowInteropHelper(owner).Handle,
            Flags = CcAnyColor | CcFullOpen,
        };
        if (!string.IsNullOrEmpty(initialHex))
        {
            info.Result = ToColorRef(initialHex);
            info.Flags |= CcRgbInit;
        }

        var pinned = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
        try
        {
            info.CustomColors = pinned.AddrOfPinnedObject();
            if (!ChooseColor(ref info))
            {
                return null;
            }
        }
        finally
        {
            pinned.Free();
        }

        // COLORREF is 0x00BBGGRR.
        var c = info.Result;
        return $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}";
    }

    private static int ToColorRef(string hex) =>
        WorkspaceColor.TryParseRgb(hex, out var r, out var g, out var b) ? r | (g << 8) | (b << 16) : 0;
}
