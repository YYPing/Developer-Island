using DeveloperIsland.Core.Localization;
using Microsoft.UI.Xaml;

namespace DeveloperIsland.UI;

/// <summary>
/// Attaches localized text to an element from XAML: <c>ui:LocText.TextKey="SettingsGeneral"</c>.
/// <para>
/// The key is a plain string, not the <see cref="Text"/> enum. The XAML type converter cannot turn
/// "SettingsGeneral" into an enum value, and a failed conversion here throws inside the window's
/// constructor: the window never appears and the failure looks like a dead button. A string key is
/// also what the tests can check against the enum without loading XAML.
/// </para>
/// <para>
/// The key stays on the element so <see cref="LocalizationHost"/> can re-read every attached key
/// when the language changes; a literal string has no such record and would stay English forever.
/// </para>
/// </summary>
public static class LocText
{
    /// <summary>The <see cref="Text"/> key name attached to an element.</summary>
    public static readonly DependencyProperty TextKeyProperty = DependencyProperty.RegisterAttached(
        "TextKey",
        typeof(string),
        typeof(LocText),
        new PropertyMetadata(null, OnTextKeyChanged));

    /// <summary>Optional <c>{0}</c>-style arguments, separated by <c>|</c>.</summary>
    public static readonly DependencyProperty ArgsProperty = DependencyProperty.RegisterAttached(
        "Args",
        typeof(string),
        typeof(LocText),
        new PropertyMetadata(null, OnTextKeyChanged));

    public static string? GetTextKey(DependencyObject element) => (string?)element.GetValue(TextKeyProperty);

    public static void SetTextKey(DependencyObject element, string? value) => element.SetValue(TextKeyProperty, value);

    public static string? GetArgs(DependencyObject element) => (string?)element.GetValue(ArgsProperty);

    public static void SetArgs(DependencyObject element, string? value) => element.SetValue(ArgsProperty, value);

    private static void OnTextKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) =>
        Apply(element);

    /// <summary>
    /// Pushes the resolved string onto whatever text surface the element has. An unknown key is
    /// ignored rather than thrown: a typo in one label must not take the whole window down.
    /// </summary>
    public static void Apply(DependencyObject element)
    {
        if (element is not FrameworkElement fe || GetTextKey(fe) is not { Length: > 0 } name)
        {
            return;
        }

        if (!Enum.TryParse<Text>(name, ignoreCase: false, out var key) || !Enum.IsDefined(key))
        {
            return;
        }

        var args = GetArgs(fe);
        var value = args is { Length: > 0 }
            ? Loc.T(key, args.Split('|', StringSplitOptions.TrimEntries).Cast<object?>().ToArray())
            : Loc.T(key);

        switch (fe)
        {
            case Microsoft.UI.Xaml.Controls.TextBlock text:
                text.Text = value;
                break;

            case Microsoft.UI.Xaml.Controls.TextBox box:
                box.PlaceholderText = value;
                break;

            // A ContentControl that already carries markup (a StackPanel, a FontIcon) is left alone:
            // replacing its content with a string would delete the icon.
            case Microsoft.UI.Xaml.Controls.ContentControl control when control.Content is null or string:
                control.Content = value;
                break;

            case Microsoft.UI.Xaml.Controls.MenuFlyoutItem item:
                item.Text = value;
                break;
        }
    }
}

/// <summary>
/// Keeps an open window's text in step with the language setting. Attached keys are re-read on every
/// element under each root; elements without a key are left alone, so user content is never touched.
/// </summary>
public static class LocalizationHost
{
    /// <summary>Applies a language and repaints the given roots.</summary>
    public static void Apply(UiLanguage language, params DependencyObject?[] roots)
    {
        Loc.Use(language);
        Repaint(roots);
    }

    /// <summary>Re-reads the current language into the given roots, without changing the language.</summary>
    public static void Repaint(params DependencyObject?[] roots)
    {
        foreach (var root in roots)
        {
            if (root is not null)
            {
                Walk(root);
            }
        }
    }

    private static void Walk(DependencyObject root)
    {
        LocText.Apply(root);

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        }
    }
}
