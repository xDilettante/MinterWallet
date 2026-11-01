using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MinterWallet.Views;

public partial class DerivationPanel : UserControl
{
    public DerivationPanel()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Flyout: null } button && button.Classes.Contains("help"))
                ToolTip.SetIsOpen(button, !ToolTip.GetIsOpen(button));
        });
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                foreach (var help in this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("help"))) ToolTip.SetIsOpen(help, false);
            if (DataContext is not DerivationViewModel model || !model.Parameters) return;
            if (e.Key == Key.Escape) { model.Parameters = false; e.Handled = true; }
            if (e.Key == Key.Enter)
            {
                if (e.Source is TextBox { Name: "FullPathInput" }) model.ApplyFullPath.Execute(null);
                else model.Apply.Execute(null);
                e.Handled = true;
            }
        };
    }
}
