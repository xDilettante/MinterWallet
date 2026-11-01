using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using MinterWallet.Views;

namespace MinterWallet.Mobile;

// Adapts the shared desktop views in place when they attach to the phone's visual tree: fixed desktop widths become
// stretchable, form fields and transaction rows use the phone's width, and QR cards stack vertically.
// The selected receiving address stays full; onboarding has its own mobile views over the shared view models.
public static class MobileLayout
{
    private static readonly AttachedProperty<bool> AdaptedProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Adapted", typeof(MobileLayout));
    private static readonly AttachedProperty<bool> PopupAdaptedProperty = AvaloniaProperty.RegisterAttached<Control, bool>("PopupAdapted", typeof(MobileLayout));

    public static void Attach()
    {
        UserControl.LoadedEvent.AddClassHandler<UserControl>((control, _) => AdaptView(control));
        Grid.LoadedEvent.AddClassHandler<Grid>((grid, _) => AdaptGrid(grid));
        DockPanel.LoadedEvent.AddClassHandler<DockPanel>((dock, _) => AdaptControl(dock));
        UniformGrid.LoadedEvent.AddClassHandler<UniformGrid>((grid, _) => AdaptControl(grid));
    }

    public static void AdaptView(Control root)
    {
        if (root is SettingsPage && root.FindControl<ContentControl>("MobileSecurity") is { Content: null } holder && root.FindAncestorOfType<MobileRoot>() is { } mobile)
            holder.Content = new MobileSecurityPanel { DataContext = mobile.Security };
        switch (root)
        {
            case DerivationPanel panel:
                if (panel.Content is Border addressPanel) addressPanel.Padding = new Thickness(12, 4);
                if (panel.FindControl<Button>("ToggleAddresses") is { } toggle)
                {
                    BindingOperations.GetBindingExpressionBase(toggle, ContentControl.ContentProperty)?.Dispose();
                    toggle.Content = new Icon { Kind = "chevron-down", Width = 18, Height = 18 };
                    toggle.Width = 36; toggle.MinWidth = 36; toggle.Padding = new Thickness(0);
                }
                break;
            case UnlockView unlock:
                AdaptUnlockView(unlock);
                break;
            case SendPage or SwapPage or BridgePage or StakingPage or TokensPage or MultisigPage or HistoryPage or AccountsPage or SettingsPage:
                AdaptSectionHeader(root);
                break;
        }

        foreach (var control in root.GetLogicalDescendants().OfType<Control>().Prepend(root).ToArray())
        {
            AdaptControl(control);
            if (control is TextBlock hintText && hintText.Classes.Contains("mobileHint")) MoveHint(hintText);
            if (control is Button { Flyout: null } hint && hint.Classes.Contains("help") && ToolTip.GetTip(hint) is string help)
            {
                hint.Flyout = MobileHelpButton.For(hint, help);
            }
            if (control is Button { Flyout: Flyout { Content: Control content } flyout } button && !button.GetValue(PopupAdaptedProperty))
            {
                button.SetValue(PopupAdaptedProperty, true);
                flyout.Opened += (_, _) => content.Width = Math.Max(160, Math.Min(360, (TopLevel.GetTopLevel(button)?.ClientSize.Width ?? 360) - 64));
            }
        }
        if (root is SettingsPage settings) AdaptSettings(settings);
        if (root is SendPage send) CompactSend(send);
        if (root is TokensPage tokens && tokens.FindControl<Border>("TokenRules") is { Child: Control rules, Parent: Panel parent } rulesBorder)
        {
            rulesBorder.Child = null;
            var help = new MobileHelpButton { Name = "TokenRulesHelp", HorizontalAlignment = HorizontalAlignment.Right };
            help.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Icon { Kind = "help" }, new TextBlock { Text = Localization.Text("Правила сети Minter для токенов") } } };
            help.Flyout = MobileHelpButton.For(help, rules);
            var at = parent.Children.IndexOf(rulesBorder); parent.Children.Remove(rulesBorder); parent.Children.Insert(at, help);
        }
    }

    private static void MoveHint(TextBlock text)
    {
        if (text.Parent is not Panel parent) return;
        var help = new MobileHelpButton { Text = text.Text, HorizontalAlignment = HorizontalAlignment.Right };
        var label = parent.Children.OfType<TextBlock>().FirstOrDefault(t => t != text && (t.Classes.Contains("label") || t.Classes.Contains("h2")));
        if (label != null)
        {
            var at = parent.Children.IndexOf(label);
            if (parent.Children.IndexOf(text) < at) at--;
            parent.Children.Remove(label); parent.Children.Remove(text);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(label); row.Children.Add(help); Grid.SetColumn(help, 1);
            parent.Children.Insert(at, row);
        }
        else
        {
            var at = parent.Children.IndexOf(text); parent.Children.Remove(text); parent.Children.Insert(at, help);
        }
    }

    private static void CompactSend(SendPage page)
    {
        if (page.FindControl<StackPanel>("SendCoinField") is not { Parent: Panel parent } coin
            || page.FindControl<StackPanel>("SendAmountField") is not { } amount
            || coin.Children.Count != 2 || amount.Children.Count < 2) return;
        var coinLabel = coin.Children[0]; var coinInput = coin.Children[1];
        var amountLabel = amount.Children[0]; var amountInput = amount.Children[1];
        coin.Children.Clear(); amount.Children.Remove(amountLabel); amount.Children.Remove(amountInput);
        var fields = new Grid { Name = "PhoneSendAmountRow", ColumnDefinitions = new ColumnDefinitions("*,120"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8 };
        fields.Children.Add(amountLabel); fields.Children.Add(coinLabel); fields.Children.Add(amountInput); fields.Children.Add(coinInput);
        Grid.SetColumn(coinLabel, 1); Grid.SetRow(amountInput, 1); Grid.SetRow(coinInput, 1); Grid.SetColumn(coinInput, 1);
        var at = parent.Children.IndexOf(coin); parent.Children.Remove(coin); parent.Children.Insert(at, fields);
        if (parent is StackPanel body) body.Spacing = 12;
        if (parent.Parent is Border card) card.Padding = new Thickness(12);
    }

    private static void AdaptUnlockView(UnlockView unlock)
    {
        if (unlock.GetValue(AdaptedProperty)) return;
        unlock.SetValue(AdaptedProperty, true);

        if (unlock.Content is not ScrollViewer { Content: StackPanel panel }) return;
        if (unlock.FindControl<TextBox>("PasswordBox") is { } field && unlock.DataContext is UnlockViewModel model && model.Credentials.BiometricsEnabled && MobileHost.Platform?.CanUseBiometrics == true)
            AutoFocus.SetIsEnabled(field, false);

        var bioButton = new Button
        {
            Name = "BiometricUnlockButton",
            Content = Localization.Text("Повторить биометрию"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsVisible = unlock.DataContext is UnlockViewModel current && current.Credentials.BiometricsEnabled && MobileHost.Platform is { CanUseBiometrics: true, HasBiometricSecret: true },
        };
        bioButton.Click += async (_, _) =>
        {
            if (unlock.DataContext is UnlockViewModel vm && unlock.FindAncestorOfType<MobileRoot>() is { } mobileRoot)
                await mobileRoot.PromptBiometricUnlockAsync(vm);
        };
        unlock.AttachedToVisualTree += (_, _) =>
        {
            bioButton.IsVisible = unlock.DataContext is UnlockViewModel vm && vm.Credentials.BiometricsEnabled && MobileHost.Platform is { CanUseBiometrics: true, HasBiometricSecret: true };
        };

        // Insert right after the primary "Разблокировать" button.
        var unlockBtnIdx = panel.Children.OfType<Button>().Select((b, idx) => (b, idx: panel.Children.IndexOf(b))).FirstOrDefault(x => x.b.IsDefault).idx;
        if (unlockBtnIdx >= 0 && unlockBtnIdx + 1 <= panel.Children.Count)
            panel.Children.Insert(unlockBtnIdx + 1, bioButton);
        else
            panel.Children.Add(bioButton);
    }

    private static void AdaptSectionHeader(Control page)
    {
        foreach (var heading in page.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("h1"))) heading.IsVisible = false;
        foreach (var intro in page.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("mobileIntro"))) intro.IsVisible = false;
        if (page is HistoryPage { Content: Grid history } && history.Children.FirstOrDefault() is DockPanel historyToolbar)
            historyToolbar.IsVisible = false; // Its actions are in the mobile section header.
        // Only explicitly marked introductory text moves to the tappable header help. Operation warnings stay in place.
    }

    private static void AdaptSettings(SettingsPage page)
    {
        if (page.GetValue(AdaptedProperty)) return;
        page.SetValue(AdaptedProperty, true);
        if (page.FindControl<StackPanel>("SecuritySettings") is { Parent: StackPanel root } security)
        {
            root.Children.Remove(security);
            root.Children.Insert(Math.Min(2, root.Children.Count), security);
            foreach (var name in new[] { "ConnectionSettings", "LanguageSettings", "AppearanceSettings", "NotificationSettings" })
            {
                if (page.FindControl<StackPanel>(name) is not { Parent: StackPanel parent } group || group.Children.FirstOrDefault() is not TextBlock heading) continue;
                var at = parent.Children.IndexOf(group);
                group.Children.Remove(heading);
                parent.Children.Remove(group);
                parent.Children.Insert(at, new Expander { Header = heading.Text, Content = group, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
        }
        if (page.FindControl<ListBox>("SettingsCredentialMode") is { Parent: Panel controls } selector)
        {
            var at = controls.Children.IndexOf(selector);
            if (at > 0 && controls.Children[at - 1] is TextBlock label) label.IsVisible = false;
            controls.Children.Remove(selector);
            var picker = new CredentialPicker();
            picker.DataContext = (controls.DataContext as SettingsViewModel)?.Credentials;
            controls.DataContextChanged += (_, _) => picker.DataContext = (controls.DataContext as SettingsViewModel)?.Credentials;
            controls.Children.Insert(at, picker);
            if (page.FindControl<TextBlock>("CredentialDescription") is { } oldDescription) oldDescription.IsVisible = false;
        }
    }

    private static void AdaptControl(Control control)
    {
        if (control.GetValue(AdaptedProperty)) return;

        switch (control)
        {
            case TextBlock compact when compact.Classes.Contains("mobileOnly"):
                compact.IsVisible = true;
                break;
            case DockPanel tools when tools.Children.OfType<Button>().Any(b => b.Name == "ContactsButton"):
                foreach (var button in tools.Children.OfType<Button>())
                {
                    button.Margin = new Thickness(0); button.Width = button.MinWidth = 44;
                    if (button.Content is StackPanel content)
                        foreach (var label in content.Children.OfType<TextBlock>()) label.IsVisible = false;
                }
                break;
            case TextBlock block when AddressMarks.GetFull(block):
                block.TextWrapping = block.Classes.Contains("addressLine") ? TextWrapping.NoWrap : TextWrapping.Wrap;
                block.TextTrimming = TextTrimming.None;
                control.SetValue(AdaptedProperty, true);
                break;

            case DockPanel dock when dock.DataContext is BalanceItem && dock.FindAncestorOfType<ComboBox>() != null && dock.FindAncestorOfType<ComboBoxItem>() == null:
                foreach (var balance in dock.Children.OfType<TextBlock>().Where(t => DockPanel.GetDock(t) == Dock.Right)) balance.IsVisible = false;
                control.SetValue(AdaptedProperty, true);
                break;
            case StackPanel row when row.Classes.Contains("phoneStack"):
                row.Orientation = Orientation.Vertical;
                row.Spacing = 8;
                foreach (var child in row.Children) child.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.SetValue(AdaptedProperty, true);
                break;
            case DockPanel dock when dock.Classes.Contains("phoneStack"):
                dock.LastChildFill = false;
                foreach (var child in dock.Children)
                {
                    DockPanel.SetDock(child, child is Button ? Dock.Bottom : Dock.Top);
                    child.HorizontalAlignment = HorizontalAlignment.Stretch;
                    if (child is Button) child.Margin = new Thickness(0, 8, 0, 0);
                }
                control.SetValue(AdaptedProperty, true);
                break;
            case DockPanel dock when dock.Children.Count == 2 && dock.Children.OfType<TextBlock>().FirstOrDefault() is { } footerText
                && dock.Children.OfType<Button>().FirstOrDefault(b => b.Classes.Contains("accent")) is { } action:
                DockPanel.SetDock(action, Dock.Bottom);
                DockPanel.SetDock(footerText, Dock.Top);
                dock.LastChildFill = false;
                action.HorizontalAlignment = HorizontalAlignment.Stretch;
                action.HorizontalContentAlignment = HorizontalAlignment.Center;
                action.Margin = new Thickness(0, 12, 0, 0);
                footerText.TextWrapping = TextWrapping.Wrap;
                control.SetValue(AdaptedProperty, true);
                break;

            case Button button when button.Classes.Contains("accent") && button.HorizontalAlignment == HorizontalAlignment.Right:
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.MinWidth = 0;
                control.SetValue(AdaptedProperty, true);
                break;
            // Onboarding & Unlock root StackPanels have fixed desktop widths (380..600) and wide margins.
            case StackPanel panel when panel.Parent is ScrollViewer { Parent: WelcomeView or CreateWalletView or ImportView or UnlockView }:
                if (!double.IsNaN(panel.Width)) { panel.MaxWidth = panel.Width; panel.Width = double.NaN; }
                panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                panel.Margin = new Thickness(14, 12, 14, 16);
                if (panel.Spacing > 10) panel.Spacing = 10;
                control.SetValue(AdaptedProperty, true);
                break;

            // Section page root StackPanels (Margin="32,28,32,32").
            case StackPanel panel when panel.Margin.Left >= 28 && panel.Margin.Right >= 28 && panel.Parent is ScrollViewer { Parent: UserControl }:
                panel.Margin = new Thickness(12, 8, 12, 16);
                if (panel.Spacing > 12) panel.Spacing = 12;
                panel.MaxWidth = Math.Min(panel.MaxWidth, 720);
                control.SetValue(AdaptedProperty, true);
                break;

            // Section page root Grids (HistoryPage, StakingPage: Margin="32,28,32,24").
            case Grid grid when grid.Margin.Left >= 28 && grid.Margin.Right >= 28 && grid.Parent is UserControl:
                grid.Margin = new Thickness(12, 8, 12, 12);
                if (grid.RowSpacing > 8) grid.RowSpacing = 8;
                control.SetValue(AdaptedProperty, true);
                break;

            // Tighten inner vertical StackPanels with desktop spacing >= 12.
            case StackPanel panel when panel.Orientation == Orientation.Vertical && panel.Spacing >= 24:
                panel.Spacing = 20;
                if (NeedsVerticalStack(panel))
                    panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.SetValue(AdaptedProperty, true);
                break;

            // Shorten segmented tab labels so they fit on a single row without wrapping.
            case ListBox list when list.Classes.Contains("segmented"):
                list.Classes.Set("mobileWrap", list.Items.Count > 3);
                list.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;

            case ListBoxItem { Content: string label } item:
                var shortened = ShortenTabLabel(label);
                if (shortened != null) item.Content = shortened;
                control.SetValue(AdaptedProperty, true);
                break;

            // Keep mono and address TextBlocks on a single line.
            case TextBlock tb when tb.Classes.Contains("mono") && tb is not { Parent: Border { Classes: var bc } } :
                tb.TextWrapping = TextWrapping.NoWrap;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                control.SetValue(AdaptedProperty, true);
                break;

            case SelectableTextBlock stb when stb.Classes.Contains("mono") && stb.MaxHeight is double.NaN or <= 0:
                stb.TextWrapping = TextWrapping.NoWrap;
                stb.TextTrimming = TextTrimming.CharacterEllipsis;
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

    private static string? ShortenTabLabel(string label) => label switch
    {
        "Одному получателю" => "Одному",
        "One recipient" => "Single",
        "Нескольким сразу" => "Нескольким",
        "Several at once" => "Multiple",
        "Обменять сейчас" => "Обмен",
        "Swap now" => "Swap",
        "Лимитный ордер" => "Ордер",
        "Limit order" => "Order",
        "Ликвидность" => "Пул",
        "Liquidity" => "Pool",
        "Продать точную сумму" => "Продать сумму",
        "Sell an exact amount" => "Sell exact",
        "Купить точную сумму" => "Купить сумму",
        "Buy an exact amount" => "Buy exact",
        "Вывести из Minter" => "Вывод",
        "Withdraw from Minter" => "Withdraw",
        "Ввести в Minter" => "Ввод",
        "Deposit to Minter" => "Deposit",
        "Мои токены" => "Мои",
        "My tokens" => "Mine",
        "Создать токен" => "Выпуск",
        "Create token" => "Create",
        "Создать пул" => "Пул",
        "Create pool" => "Pool",
        _ => null,
    };

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
            border.Child = new StackPanel { Spacing = 6, Children = { label, controls } };
        }
        else if (dock.Parent is Panel parent && parent.Children.IndexOf(dock) is >= 0 and var index)
        {
            var margin = dock.Margin;
            dock.Children.Clear();
            controls.HorizontalAlignment = HorizontalAlignment.Left;
            parent.Children[index] = new StackPanel { Spacing = 4, Margin = margin, Children = { label, controls } };
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

        if (grid.Classes.Contains("phoneRoute"))
        {
            SetGrid(grid, "*,24,*"); grid.ColumnSpacing = 8;
            if (grid.Children.OfType<StackPanel>().FirstOrDefault(s => Grid.GetColumn(s) == 1) is { } route)
            {
                var arrow = new TextBlock { Text = "→", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
                route.Children.Clear(); route.Children.Add(arrow);
                route.HorizontalAlignment = HorizontalAlignment.Center;
            }
            foreach (var text in grid.GetLogicalDescendants().OfType<TextBlock>())
            {
                if (text.Text == "Minter Network") text.Text = "Minter";
                if (text.Classes.Contains("caption")) text.IsVisible = false;
                text.TextWrapping = TextWrapping.Wrap;
            }
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        if (grid.Classes.Contains("phoneFields"))
        {
            SetGrid(grid, "*", string.Join(',', grid.Children.Select(_ => "Auto")));
            grid.RowSpacing = 14;
            for (var i = 0; i < grid.Children.Count; i++) { Grid.SetColumn(grid.Children[i], 0); Grid.SetRow(grid.Children[i], i); }
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        if (grid.Classes.Contains("phoneTokenPair"))
        {
            SetGrid(grid, "*,*"); grid.ColumnSpacing = 8;
            foreach (var label in grid.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<TextBlock>().Take(1)))
            { label.MinHeight = 32; label.FontSize = 12; label.TextWrapping = TextWrapping.Wrap; }
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        if (grid.ColumnDefinitions is [{ Width.IsStar: true }, { Width: { IsAbsolute: true, Value: 130 } }, { Width.IsAuto: true }]
            && grid.Children.OfType<TextBox>().FirstOrDefault() is { } signerAddress
            && grid.Children.OfType<NumericUpDown>().FirstOrDefault() is { } weight
            && grid.Children.OfType<Button>().FirstOrDefault() is { } removeSigner)
        {
            SetGrid(grid, "*,130,Auto", "Auto,Auto"); grid.RowSpacing = 8;
            Grid.SetRow(signerAddress, 0); Grid.SetColumn(signerAddress, 0); Grid.SetColumnSpan(signerAddress, 3);
            Grid.SetRow(weight, 1); Grid.SetColumn(weight, 1);
            Grid.SetRow(removeSigner, 1); Grid.SetColumn(removeSigner, 2);
            var caption = new TextBlock { Text = Localization.Text("Вес"), VerticalAlignment = VerticalAlignment.Center, Classes = { "caption" } };
            Grid.SetRow(caption, 1); grid.Children.Add(caption);
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        if (grid.Children.OfType<TextBlock>().FirstOrDefault(AddressMarks.GetFull) is { } address
            && grid.Children.OfType<Button>().FirstOrDefault() is { } copy)
        {
            SetGrid(grid, "*", "Auto,Auto");
            grid.RowSpacing = 8;
            address.FontSize = 13;
            address.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(copy, 0); Grid.SetRow(copy, 1);
            copy.HorizontalAlignment = HorizontalAlignment.Right;
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // Transaction cards: give the address and amounts the full available width instead of squeezing three columns.
        if (grid.DataContext is HistoryItem && grid.Children.OfType<StackPanel>().FirstOrDefault(s => Grid.GetColumn(s) == 2 && s.MinWidth >= 120) is { } amounts
            && grid.Children.OfType<StackPanel>().FirstOrDefault(s => Grid.GetColumn(s) == 1) is { } summary)
        {
            SetGrid(grid, "Auto,*", "Auto,Auto,Auto");
            grid.ColumnSpacing = 12; grid.RowSpacing = 8;
            summary.Margin = new Thickness(0);
            amounts.MinWidth = 0;
            Grid.SetColumn(amounts, 1); Grid.SetRow(amounts, 1);
            foreach (var child in amounts.Children) child.HorizontalAlignment = HorizontalAlignment.Left;
            if (amounts.Children.LastOrDefault() is TextBlock transactionStamp)
            {
                amounts.Children.Remove(transactionStamp);
                Grid.SetColumn(transactionStamp, 0); Grid.SetColumnSpan(transactionStamp, 2); Grid.SetRow(transactionStamp, 2);
                transactionStamp.TextWrapping = TextWrapping.Wrap;
                grid.Children.Add(transactionStamp);
            }
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // Two-column QR card (OverviewPage receive card, SendPage issued check): stack QR compactly above details.
        if (grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 0
            && grid.Children.OfType<QrView>().FirstOrDefault() is { } qr
            && grid.Children.OfType<StackPanel>().FirstOrDefault(s => Grid.GetColumn(s) == 1) is { } details)
        {
            SetGrid(grid, "*", "Auto,Auto");
            grid.ColumnSpacing = 0;
            grid.RowSpacing = 8;
            qr.Width = 148;
            qr.Height = 148;
            qr.HorizontalAlignment = HorizontalAlignment.Center;
            details.Spacing = 6;
            Grid.SetColumn(qr, 0); Grid.SetRow(qr, 0);
            Grid.SetColumn(details, 0); Grid.SetRow(details, 1);
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // Multisend row (26,*,120,130,Auto -> 22,*,76,92,Auto).
        if (grid.ColumnDefinitions is [{ Width: { IsAbsolute: true, Value: 26 } }, { Width.IsStar: true }, { Width: { IsAbsolute: true, Value: 120 } }, { Width: { IsAbsolute: true, Value: 130 } }, { Width.IsAuto: true }])
        {
            var number = grid.Children.FirstOrDefault(c => Grid.GetColumn(c) == 0);
            var recipient = grid.Children.FirstOrDefault(c => Grid.GetColumn(c) == 1);
            var amount = grid.Children.FirstOrDefault(c => Grid.GetColumn(c) == 2);
            var coin = grid.Children.FirstOrDefault(c => Grid.GetColumn(c) == 3);
            var remove = grid.Children.FirstOrDefault(c => Grid.GetColumn(c) == 4);
            SetGrid(grid, "*,110", "Auto,Auto,Auto");
            grid.ColumnSpacing = 10; grid.RowSpacing = 8;
            if (number != null) { Grid.SetColumn(number, 0); Grid.SetRow(number, 0); }
            if (remove != null) { Grid.SetColumn(remove, 1); Grid.SetRow(remove, 0); remove.HorizontalAlignment = HorizontalAlignment.Right; }
            if (recipient != null) { Grid.SetColumn(recipient, 0); Grid.SetRow(recipient, 1); Grid.SetColumnSpan(recipient, 2); }
            if (amount != null) { Grid.SetColumn(amount, 0); Grid.SetRow(amount, 2); }
            if (coin != null) { Grid.SetColumn(coin, 1); Grid.SetRow(coin, 2); }
            grid.SetValue(AdaptedProperty, true);
            return;
        }

        // SwapPage pay/receive row (*,190 -> *,182).
        if (grid.ColumnDefinitions is [{ Width.IsStar: true }, { Width: { IsAbsolute: true, Value: 190 } }])
        {
            SetGrid(grid, "*,140");
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
            grid.RowSpacing = 6;
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
            grid.RowSpacing = 6;
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
