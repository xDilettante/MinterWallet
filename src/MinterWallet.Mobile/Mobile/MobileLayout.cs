using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using MinterWallet.Views;

namespace MinterWallet.Mobile;

// Adapts the shared desktop views in place when they attach to the phone's visual tree: fixed desktop widths become
// stretchable, wide outer margins shrink to fit a narrow screen, two-column QR cards stack vertically, 3-column word
// grids become 2-column so long BIP39 words never clip, and desktop-only tray settings hide.
public static class MobileLayout
{
    private static readonly AttachedProperty<bool> AdaptedProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Adapted", typeof(MobileLayout));

    public static void Attach()
    {
        UserControl.LoadedEvent.AddClassHandler<UserControl>((control, _) => AdaptView(control));
        Grid.LoadedEvent.AddClassHandler<Grid>((grid, _) => AdaptGrid(grid));
        UniformGrid.LoadedEvent.AddClassHandler<UniformGrid>((grid, _) => AdaptControl(grid));
    }

    public static void AdaptView(Control root)
    {
        foreach (var control in root.GetLogicalDescendants().OfType<Control>().Prepend(root).ToArray())
            AdaptControl(control);
    }

    private static void AdaptControl(Control control)
    {
        if (control.GetValue(AdaptedProperty)) return;

        switch (control)
        {
            // Onboarding & Unlock root StackPanels have fixed desktop widths (380..600) and wide margins.
            case StackPanel panel when panel.Parent is ScrollViewer { Parent: WelcomeView or CreateWalletView or ImportView or UnlockView }:
                if (!double.IsNaN(panel.Width)) { panel.MaxWidth = panel.Width; panel.Width = double.NaN; }
                panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                panel.Margin = new Thickness(16, 20, 16, 24);
                control.SetValue(AdaptedProperty, true);
                break;

            // Section page root StackPanels (Margin="32,28,32,32").
            case StackPanel panel when panel.Margin.Left >= 28 && panel.Margin.Right >= 28 && panel.Parent is ScrollViewer { Parent: UserControl }:
                panel.Margin = new Thickness(14, 16, 14, 24);
                control.SetValue(AdaptedProperty, true);
                break;

            // Section page root Grids (HistoryPage, StakingPage: Margin="32,28,32,24").
            case Grid grid when grid.Margin.Left >= 28 && grid.Margin.Right >= 28 && grid.Parent is UserControl:
                grid.Margin = new Thickness(14, 16, 14, 16);
                control.SetValue(AdaptedProperty, true);
                break;

            // 3-column mnemonic word grids -> 2 columns on a phone so 12/24 words have room.
            case UniformGrid uniform when uniform.Columns == 3:
                uniform.Columns = 2;
                control.SetValue(AdaptedProperty, true);
                break;

            // Step header text ("2. Проверка — по желанию") wraps instead of overflowing its 1/3 column.
            case Border { Classes: var c } border when c.Contains("step") && border.Child is TextBlock stepText:
                stepText.TextWrapping = TextWrapping.Wrap;
                control.SetValue(AdaptedProperty, true);
                break;

            // Horizontal StackPanels that pair a segmented picker or NumericUpDown with wider controls/text.
            case StackPanel { Orientation: Orientation.Horizontal } row when NeedsVerticalStack(row):
                row.Orientation = Orientation.Vertical;
                row.HorizontalAlignment = HorizontalAlignment.Stretch;
                foreach (var child in row.Children.OfType<ComboBox>()) child.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.SetValue(AdaptedProperty, true);
                break;

            // Expanded history details indented by 54px for the desktop avatar: use full width on a phone.
            case Border { Margin.Left: >= 48 } well when well.Classes.Contains("well"):
                well.Margin = new Thickness(0, 6, 0, 0);
                control.SetValue(AdaptedProperty, true);
                break;

            // Slippage DockPanel on SwapPage or Available + percentage buttons DockPanel on SendPage: stack label above controls.
            case DockPanel dock when dock.Children.Count == 2 && dock.Children.OfType<TextBlock>().Any()
                && dock.Children.OfType<StackPanel>().Any(s => s.Children.OfType<ListBox>().Any() || s.Children.Count == 4 && s.Children.OfType<Button>().Count() == 4):
                ConvertDockToVertical(dock);
                control.SetValue(AdaptedProperty, true);
                break;

            // Bridge route banner arrows ("────►" is too wide for 3 columns on a phone).
            case TextBlock { Text: "────►" } arrow:
                arrow.Text = "→";
                control.SetValue(AdaptedProperty, true);
                break;

            // SettingsPage: hide the desktop-only "Окно и трей" ("Window and tray") section.
            case StackPanel group when group.Children.FirstOrDefault() is TextBlock { Text: "Окно и трей" or "Window and tray" }
                && group.Children.OfType<CheckBox>().Any():
                group.IsVisible = false;
                control.SetValue(AdaptedProperty, true);
                break;

            // Flyout panels with fixed desktop widths (360..380).
            case Control { Width: >= 340 and <= 400 } flyoutChild when flyoutChild is StackPanel or ListBox && !(flyoutChild.Parent is ScrollViewer):
                flyoutChild.Width = 310;
                control.SetValue(AdaptedProperty, true);
                break;

            case Grid grid:
                AdaptGrid(grid);
                break;
        }
    }

    private static bool NeedsVerticalStack(StackPanel row)
    {
        // CreateWalletView: 12/24 words segmented box + explanation text.
        if (row.Children.Count == 2 && row.Children[0] is ListBox && row.Children[1] is TextBlock) return true;
        // MultisigPage: "Добавить участника" button + "Из счетов и контактов…" ComboBox (MinWidth 240).
        if (row.Children.Count == 2 && row.Children[0] is Button && row.Children[1] is ComboBox { MinWidth: >= 200 }) return true;
        // MultisigPage: threshold NumericUpDown + 3 preset buttons.
        if (row.Children.Count == 4 && row.Children[0] is NumericUpDown) return true;
        // SwapPage liquidity withdrawal row ("Вывести из выбранной:" + 3 buttons).
        if (row.Children.Count == 4 && row.Children[0] is TextBlock && row.Children[1] is Button) return true;
        return false;
    }

    private static void ConvertDockToVertical(DockPanel dock)
    {
        var label = dock.Children.OfType<TextBlock>().First();
        var controls = dock.Children.OfType<StackPanel>().First();
        if (dock.Parent is Border border)
        {
            dock.Children.Clear();
            controls.HorizontalAlignment = HorizontalAlignment.Left;
            border.Child = new StackPanel { Spacing = 8, Children = { label, controls } };
        }
        else if (dock.Parent is Panel parent && parent.Children.IndexOf(dock) is >= 0 and var index)
        {
            var margin = dock.Margin;
            dock.Children.Clear();
            controls.HorizontalAlignment = HorizontalAlignment.Left;
            parent.Children[index] = new StackPanel { Spacing = 6, Margin = margin, Children = { label, controls } };
        }
    }

    private static void SetGrid(Grid grid, string? columns = null, string? rows = null)
    {
        if (columns != null)
        {
            grid.ColumnDefinitions.Clear();
            foreach (var col in new ColumnDefinitions(columns)) grid.ColumnDefinitions.Add(col);
        }
        if (rows != null)
        {
            grid.RowDefinitions.Clear();
            foreach (var row in new RowDefinitions(rows)) grid.RowDefinitions.Add(row);
        }
        grid.InvalidateMeasure();
    }

    private static void AdaptGrid(Grid grid)
    {
        if (grid.GetValue(AdaptedProperty)) return;

        // Two-column QR card (OverviewPage receive card, SendPage issued check): stack QR above details.
        if (grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 0
            && grid.Children.OfType<QrView>().FirstOrDefault() is { } qr
            && grid.Children.OfType<StackPanel>().FirstOrDefault(s => Grid.GetColumn(s) == 1) is { } details)
        {
            SetGrid(grid, "*", "Auto,Auto");
            grid.ColumnSpacing = 0;
            grid.RowSpacing = 14;
            qr.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(qr, 0); Grid.SetRow(qr, 0);
            Grid.SetColumn(details, 0); Grid.SetRow(details, 1);
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // Multisend row (26,*,120,130,Auto -> 22,*,76,92,Auto).
        if (grid.ColumnDefinitions is [{ Width: { IsAbsolute: true, Value: 26 } }, { Width.IsStar: true }, { Width: { IsAbsolute: true, Value: 120 } }, { Width: { IsAbsolute: true, Value: 130 } }, { Width.IsAuto: true }])
        {
            SetGrid(grid, "22,*,76,92,Auto");
            grid.ColumnSpacing = 6;
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // SwapPage pay/receive row (*,190 -> *,168).
        if (grid.ColumnDefinitions is [{ Width.IsStar: true }, { Width: { IsAbsolute: true, Value: 190 } }])
        {
            SetGrid(grid, "*,168");
            grid.ColumnSpacing = 8;
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // HistoryPage statusBanner (Auto,*,Auto -> two rows so status text and timestamp never collide).
        if (grid.Parent is Border { Classes: var bc } && bc.Contains("statusBanner")
            && grid.ColumnDefinitions.Count == 3 && grid.Children.Count == 3 && grid.Children[2] is TextBlock stamp)
        {
            SetGrid(grid, "Auto,*", "Auto,Auto");
            grid.RowSpacing = 2;
            Grid.SetRow(stamp, 1); Grid.SetColumn(stamp, 1);
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // StakingPage delegation row (200,*,Auto -> two rows: coin + amount on row 0, buttons on row 1).
        if (grid.ColumnDefinitions is [{ Width: { IsAbsolute: true, Value: 200 } }, { Width.IsStar: true }, { Width.IsAuto: true }]
            && grid.Children.Count == 3 && grid.Children[2] is StackPanel buttons)
        {
            SetGrid(grid, "130,*", "Auto,Auto");
            grid.RowSpacing = 8;
            Grid.SetRow(buttons, 1); Grid.SetColumn(buttons, 0); Grid.SetColumnSpan(buttons, 2);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // StakingPage validator vote row (*,*,Auto -> buttons on row 1).
        if (grid.ColumnDefinitions is [{ Width.IsStar: true }, { Width.IsStar: true }, { Width.IsAuto: true }]
            && grid.Children.Count == 3 && grid.Children[2] is Button voteButton)
        {
            SetGrid(grid, "*,*", "Auto,Auto");
            grid.RowSpacing = 8;
            Grid.SetRow(voteButton, 1); Grid.SetColumn(voteButton, 0); Grid.SetColumnSpan(voteButton, 2);
            voteButton.HorizontalAlignment = HorizontalAlignment.Right;
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // HistoryPage detail row (140,* -> 104,*).
        if (grid.ColumnDefinitions is [{ Width: { IsAbsolute: true, Value: 140 } }, { Width.IsStar: true }])
        {
            SetGrid(grid, "104,*");
            grid.ColumnSpacing = 8;
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // OverviewPage / HistoryPage transaction item right column (MinWidth="130" -> MinWidth="88").
        if (grid.ColumnDefinitions.Count == 3 && grid.Children.OfType<StackPanel>().FirstOrDefault(s => Grid.GetColumn(s) == 2 && s.MinWidth >= 120) is { } rightCol)
        {
            rightCol.MinWidth = 88;
            grid.SetValue(AdaptedProperty, true);
        }
    }
}
