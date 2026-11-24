using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TCV.App.Workspaces;

namespace TCV.App.Controls;

/// <summary>
/// A workspace as a list names it: its picture and name, or, with no picture to show, the text with its
/// emoji (ADR-0044). One control for the Workspaces menu, the workspace box in the title bar and the
/// switcher. Font and colour come from whoever holds it. The picture comes from the store's memory, so
/// opening a menu reads nothing on the UI thread (INV-06); one not read yet is fetched in the background
/// and takes its place when it arrives. Until then, and when the file is gone, the emoji stands in, as on
/// the rail.
/// </summary>
public sealed class WorkspaceLabel : Grid
{
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(
        nameof(Picture), typeof(string), typeof(WorkspaceLabel),
        new PropertyMetadata(string.Empty, (d, _) => ((WorkspaceLabel)d).OnPictureChanged()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(WorkspaceLabel),
        new PropertyMetadata(string.Empty, (d, _) => ((WorkspaceLabel)d).Show()));

    public static readonly DependencyProperty TextBesidePictureProperty = DependencyProperty.Register(
        nameof(TextBesidePicture), typeof(string), typeof(WorkspaceLabel),
        new PropertyMetadata(string.Empty, (d, _) => ((WorkspaceLabel)d).Show()));

    private readonly Image _image = new()
    {
        Width = 16,
        Height = 16,
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _text = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public WorkspaceLabel()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        SetColumn(_text, 1);
        Children.Add(_image);
        Children.Add(_text);
    }

    /// <summary>The file name of the picture in the store (<see cref="Workspace.IconImage"/>); empty — none.</summary>
    public string? Picture
    {
        get => (string?)GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    /// <summary>What it says with no picture to show: the name with its emoji.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>What it says beside the picture: the name alone, the picture stands in place of the emoji.</summary>
    public string? TextBesidePicture
    {
        get => (string?)GetValue(TextBesidePictureProperty);
        set => SetValue(TextBesidePictureProperty, value);
    }

    private void OnPictureChanged()
    {
        _image.Source = WorkspaceIconStore.Default.Peek(Picture);
        Show();
        if (_image.Source is null && !string.IsNullOrEmpty(Picture))
        {
            _ = FetchAsync(Picture);
        }
    }

    private async Task FetchAsync(string fileName)
    {
        var picture = await WorkspaceIconStore.Default.LoadAsync(fileName);
        if (picture is not null && fileName == Picture)
        {
            _image.Source = picture;
            Show();
        }
    }

    private void Show()
    {
        var hasPicture = _image.Source is not null;
        _image.Visibility = hasPicture ? Visibility.Visible : Visibility.Collapsed;
        _text.Text = hasPicture && !string.IsNullOrEmpty(TextBesidePicture) ? TextBesidePicture : Text;
    }
}
