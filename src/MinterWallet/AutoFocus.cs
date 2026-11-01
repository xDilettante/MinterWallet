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
            EventHandler onActivated = (_, _) => Later(control);
            EventHandler<AvaloniaPropertyChangedEventArgs> onVisible = (_, change) =>
            {
                if (change.Property == Visual.IsVisibleProperty && change.NewValue is true) Later(control);
            };
            Window? attachedWindow = null;
            var attachedAncestors = new List<Visual>();

            control.Loaded += (_, _) => Later(control);
            control.PropertyChanged += onVisible;
            control.AttachedToVisualTree += (_, _) =>
            {
                if (TopLevel.GetTopLevel(control) is Window window) { attachedWindow = window; window.Activated += onActivated; }
                // A step of a form shows by its panel becoming visible, not the field itself.
                foreach (var ancestor in control.GetVisualAncestors())
                {
                    attachedAncestors.Add(ancestor);
                    ancestor.PropertyChanged += onVisible;
                }
            };
            control.DetachedFromVisualTree += (_, _) =>
            {
                if (attachedWindow != null) { attachedWindow.Activated -= onActivated; attachedWindow = null; }
                foreach (var ancestor in attachedAncestors) ancestor.PropertyChanged -= onVisible;
                attachedAncestors.Clear();
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
