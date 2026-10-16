using TCV.Core.Interfaces;

namespace TCV.Core.PluginSdk;

public interface IColumnPlugin : IPlugin
{
    IReadOnlyList<ColumnDefinition> Columns { get; }
    object? GetValue(IFileEntry entry, string columnId);
}
