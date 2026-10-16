namespace TCV.Core.PluginSdk;

public sealed record ColumnDefinition(
    string Id,
    string Title,
    double DefaultWidth = 100,
    bool IsVisible = true);
