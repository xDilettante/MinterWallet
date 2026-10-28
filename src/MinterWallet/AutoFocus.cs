using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MinterWallet;

// AutoFocus.IsEnabled="True": the control takes the keyboard focus whenever it appears — once laid out, when it becomes
// visible (a step of a form) and when its window is activated — so the password can be typed at once.
public static class AutoFocus
{
    public static readonly AttachedProperty<bool> IsEnabledProperty = AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(AutoFocus));

    static AutoFocus()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true) return;
            control.Loaded += (_, _) => Later(control);
            control.PropertyChanged += (_, change) => { if (change.Property == Visual.IsVisibleProperty && change.NewValue is true) Later(control); };
            control.AttachedToVisualTree += (_, _) =>
            {
                if (TopLevel.GetTopLevel(control) is Window window) window.Activated += (_, _) => Later(control);
                // A step of a form shows by its panel becoming visible, not the field itself.
                foreach (var ancestor in control.GetVisualAncestors())
                    ancestor.PropertyChanged += (_, change) => { if (change.Property == Visual.IsVisibleProperty && change.NewValue is true) Later(control); };
            };
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void Later(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (control.IsEffectivelyVisible && control.IsEffectivelyEnabled) control.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Input);
}
