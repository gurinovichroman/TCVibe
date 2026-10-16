using System.Windows.Automation;
using System.Windows.Controls;

namespace TCV.App.Controls;

/// <summary>
/// The panel's quick filter box, under the path bar; its data context is the panel's list. Typing into the
/// list opens it with the keys in it, so the text can be edited like any text (caret, paste, a space in
/// a name). It keeps only the keys that edit its text (<see cref="QuickFilterKeys"/>): MainWindow sends the
/// rest to the list's commands, and the panel moves the cursor with ↑ ↓. The list leaves the keys here
/// when its cursor moves or its rows are refilled (<see cref="FileListControl"/>), which is why this is a
/// type of its own.
/// </summary>
public sealed class QuickFilterBox : TextBox
{
    public QuickFilterBox()
    {
        // A subclass does not get the theme's implicit TextBox style by itself (TRAP-68).
        SetResourceReference(StyleProperty, typeof(TextBox));
        AutomationProperties.SetAutomationId(this, "QuickFilterBox");
    }

    /// <summary>Takes the keys with the caret after the text, where the next letter goes.</summary>
    public void TakeKeys()
    {
        if (!IsVisible)
        {
            return;
        }

        Focus();
        Select(Text.Length, 0);
    }
}
