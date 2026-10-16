using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace TCV.App.Localization;

/// <summary>
/// The source XAML binds to for a string that names a shortcut (<c>{gesture:…}</c>, see
/// <see cref="GestureTokens"/>). <c>{DynamicResource}</c> would show the raw token and miss a rebind;
/// this reads the string through <see cref="LocalizationManager.Get"/> and tells the bindings to read
/// it again after a language switch or a rebind. Used through <see cref="LiveExtension"/>.
/// </summary>
public sealed class LiveText : INotifyPropertyChanged
{
    public static LiveText Instance { get; } = new();

    private LiveText()
    {
        LocalizationManager.LanguageChanged += (_, _) => Refresh();
        LocalizationManager.GesturesChanged += (_, _) => Refresh();
    }

    public string this[string key] => LocalizationManager.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}

/// <summary>
/// <c>ToolTip="{loc:Live Tab_CloseTip}"</c>: a localised string that follows both the language and the
/// shortcut bindings. Use it instead of <c>{DynamicResource}</c> for any key with a <c>{gesture:…}</c>
/// token; <c>LocalizationGuardTests</c> checks that.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LiveExtension : MarkupExtension
{
    public LiveExtension()
    {
    }

    public LiveExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LiveText.Instance, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}
