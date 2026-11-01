using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MinterWallet.Views;

namespace MinterWallet.Mobile;

public partial class MobileMainView : UserControl
{
    private MainViewModel? main;
    private readonly Dictionary<Section, Control> pages = [];

    public MobileMainView()
    {
        InitializeComponent();
        WalletMenu.Flyout!.Closed += (_, _) => { main?.AccountsPage.CancelRemove.Execute(null); WalletRecoverySaved.IsChecked = false; };
        More.SizeChanged += (_, e) => MoreCard.MaxHeight = Math.Max(160, e.NewSize.Height - 12);
        DataContextChanged += (_, _) =>
        {
            if (main != null) main.PropertyChanged -= OnMainChanged;
            main = DataContext as MainViewModel;
            pages.Clear();
            Pages.Children.Clear();
            if (main != null)
            {
                if (main.Derivation.Expanded) main.Derivation.Toggle.Execute(null);
                main.PropertyChanged += OnMainChanged;
                ShowActivePage();
            }
            MarkMore();
        };
    }

    // The system's back: the «Ещё» sheet closes, a section goes back to the overview; false on the overview itself.
    public bool Back(MainViewModel model)
    {
        if (this.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Flyout?.IsOpen == true) is { } popup)
        { popup.Flyout!.Hide(); return true; }
        if (More.IsVisible) { More.IsVisible = false; return true; }
        if (model.ShowReceive)
        {
            model.ShowReceive = false;
            return true;
        }
        if (model.IsOverview) return false;
        model.Section = Section.Overview;
        return true;
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SectionIndex))
        {
            More.IsVisible = false;
            ShowActivePage();
            MarkMore();
        }
    }

    private void ShowActivePage()
    {
        if (main == null) return;
        var section = main.Section;
        if (!pages.TryGetValue(section, out var view))
        {
            view = section switch
            {
                Section.Overview => new MobileOverviewView(),
                Section.Send => new SendPage(),
                Section.Swap => new SwapPage(),
                Section.Bridge => new BridgePage(),
                Section.Staking => new StakingPage(),
                Section.Tokens => new TokensPage(),
                Section.Multisig => new MultisigPage(),
                Section.History => new HistoryPage(),
                Section.Accounts => new AccountsPage(),
                Section.Settings => new SettingsPage(),
                _ => new MobileOverviewView(),
            };
            MobileLayout.AdaptView(view);
            if (section == Section.Overview)
            {
                var refresh = new RefreshContainer { Name = "PullToRefresh", Content = view, PullDirection = PullDirection.TopToBottom };
                refresh.RefreshRequested += async (_, e) =>
                {
                    var deferral = e.GetDeferral();
                    try { if (main?.IsOverview == true) await main.Refresh.RunAsync(); }
                    finally { deferral.Complete(); }
                };
                view = refresh;
            }
            pages[section] = view;
        }
        if (Pages.Children.Count != 1 || !ReferenceEquals(Pages.Children[0], view))
        {
            Pages.Children.Clear();
            Pages.Children.Add(view);
        }
        ContextHelp.Text = string.Join("\n\n", view.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("mobileIntro")).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
        ContextHelp.IsVisible = !string.IsNullOrWhiteSpace(ContextHelp.Text);
    }

    private void OnWalletSelected(object? sender, SelectionChangedEventArgs e) { if (e.AddedItems.Count > 0) WalletMenu.Flyout?.Hide(); }

    // «Ещё» lights up while one of the sections under it is open.
    private void MarkMore() => NavMore.Classes.Set("selected", main != null && !(main.IsOverview || main.IsStaking || main.IsSwap || main.IsHistory));

    private void OnSection(object? sender, RoutedEventArgs e)
    {
        if (main == null || sender is not Control { Tag: string tag } || !Enum.TryParse<Section>(tag, out var section)) return;
        More.IsVisible = false;
        main.Section = section;
    }

    private void OnMore(object? sender, RoutedEventArgs e) => More.IsVisible = !More.IsVisible;

    private void OnAddWallet(object? sender, RoutedEventArgs e)
    {
        WalletMenu.Flyout?.Hide();
        More.IsVisible = false;
        main?.AddAddress.Execute(null);
    }

    private void OnRemoveWallet(object? sender, RoutedEventArgs e)
    {
        WalletRecoverySaved.IsChecked = false;
        if (main?.AccountsPage.Active != null) main.AccountsPage.ConfirmingGroupRemove = true;
    }

    private async void OnConfirmRemoveWallet(object? sender, RoutedEventArgs e)
    {
        if (main is not { } model || WalletRecoverySaved.IsChecked != true) return;
        WalletMenu.Flyout?.Hide();
        await model.AccountsPage.DeleteWallet.RunAsync();
        if (ReferenceEquals(main, model) && ReferenceEquals(model.Shell.Page, model))
        { model.Say(model.AccountsPage.Status, model.AccountsPage.Tone); model.Section = Section.Overview; }
    }

    private void OnMoreBackdrop(object? sender, PointerPressedEventArgs e) => More.IsVisible = false;
}
