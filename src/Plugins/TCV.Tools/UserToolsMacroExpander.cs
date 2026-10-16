using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.Tools;

/// <summary>
/// Expands macro tokens in UserTool Command / Arguments / WorkingDir strings.
/// <list type="table">
///   <item><term>%P</term><description>Active panel path (directory)</description></item>
///   <item><term>%N</term><description>Name of the focused file without extension</description></item>
///   <item><term>%E</term><description>Extension of the focused file (without leading dot)</description></item>
///   <item><term>%S</term><description>Space-separated quoted paths of all selected files</description></item>
///   <item><term>%T</term><description>Opposite panel / target path</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Expansion is single-pass and goes through <see cref="MacroTemplate"/>, so a file called
/// <c>a%Sb.txt</c> is never expanded a second time, and values are quoted by the same routine as
/// every other launcher (<see cref="ShellQuoting"/>): a blank or a <c>cmd.exe</c> operator such as
/// <c>&amp;</c> gets the path quoted, and a drive root inside the template's own quotes keeps its
/// closing quote.
/// <para>
/// %P and %T are folders only when the panel's file system is the disk (scheme <c>file</c>): an
/// archive's or a server's "/pub" is no Windows path, and turned into "\pub" it named a folder on
/// the current drive. Such a macro throws <see cref="PanelFolderUnavailableException"/> instead —
/// and only when the template uses it, so a tool without %T still runs beside an open archive.
/// </para>
/// </remarks>
public static class UserToolsMacroExpander
{
    /// <summary>
    /// Expands an <b>argument</b> string: values are quoted as needed. Pass
    /// <paramref name="forCmd"/> when the program may be run by <c>cmd.exe</c>; values with
    /// <c>%</c> or <c>!</c> then throw <see cref="UnsafeMacroValueException"/>.
    /// </summary>
    public static string Expand(string template, IToolContext context, bool forCmd = false) =>
        ExpandCore(template, context, raw: false, forCmd);

    /// <summary>Expands a program path or working directory: values are inserted as they are.</summary>
    public static string ExpandRaw(string template, IToolContext context) =>
        ExpandCore(template, context, raw: true, forCmd: false);

    private static string ExpandCore(string template, IToolContext context, bool raw, bool forCmd)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        var activePath = DiskFolderOf(context.ActiveFileSystem, context.ActivePath);
        var oppositePath = DiskFolderOf(context.OppositeFileSystem, context.OppositePath);

        // The focused file is the first selected one (or none).
        var focused = context.SelectedFiles.FirstOrDefault();
        var focusedName = focused is not null ? focused.Name : string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(focusedName);
        var extension = Path.GetExtension(focusedName).TrimStart('.');

        IReadOnlyList<string>? Resolve(char macro) => macro switch
        {
            'P' => [activePath ?? throw new PanelFolderUnavailableException('P')],
            'T' => [oppositePath ?? throw new PanelFolderUnavailableException('T')],
            'N' => nameWithoutExt.Length == 0 ? [] : [nameWithoutExt],
            'E' => extension.Length == 0 ? [] : [extension],
            'S' => context.SelectedFiles.Select(f => ToWindows(f.Path.Value)).ToList(),
            _ => null,
        };

        return MacroTemplate.Expand(
            template, Resolve, raw, eatSpaceBeforeEmpty: false,
            validate: forCmd ? ShellQuoting.IsSafeForCmd : null);
    }

    /// <summary>
    /// A panel's folder as a Windows path, or null when the panel shows no folder on the disk (an
    /// archive, a server — or, as the host reports it, the search-results list).
    /// </summary>
    public static string? DiskFolderOf(IVirtualFileSystem? fileSystem, VfsPath path) =>
        string.Equals(fileSystem?.Scheme, "file", StringComparison.OrdinalIgnoreCase)
            ? ToWindows(path.Value)
            : null;

    private static string ToWindows(string vfsPath) => vfsPath.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>
/// A tool's template uses %P or %T, and that panel shows no folder on the disk: an archive, a
/// server or the search-results list. Refused rather than expanded to a path that names something
/// else.
/// </summary>
public sealed class PanelFolderUnavailableException(char macro)
    : InvalidOperationException($"%{macro} names a panel that shows no folder on the disk.")
{
    /// <summary>The macro letter: 'P' for the active panel, 'T' for the other one.</summary>
    public char Macro { get; } = macro;
}
