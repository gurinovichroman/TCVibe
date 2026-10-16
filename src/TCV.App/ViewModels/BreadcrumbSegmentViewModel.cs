using CommunityToolkit.Mvvm.ComponentModel;

namespace TCV.App.ViewModels;

public partial class BreadcrumbSegmentViewModel : ObservableObject
{

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>The root: no separator in front of it.</summary>
    public bool IsFirst { get; set; }

    /// <summary>The folder the panel is in: written out in the text colour after a chevron, the way
    /// there quieter (the design's «C: / Projects › TotalCommanderVibe»).</summary>
    public bool IsLast { get; set; }
}
