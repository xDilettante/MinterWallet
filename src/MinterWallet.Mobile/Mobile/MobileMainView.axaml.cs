using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MinterWallet.Mobile;

public partial class MobileMainView : UserControl
{
    private MainViewModel? main;

    public MobileMainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (main != null) main.PropertyChanged -= OnMainChanged;
            main = DataContext as MainViewModel;
            if (main != null) main.PropertyChanged += OnMainChanged;
            MarkMore();
        };
    }

    // The system's back: the «Ещё» sheet closes, a section goes back to the overview; false on the overview itself.
    public bool Back(MainViewModel model)
    {
        if (More.IsVisible) { More.IsVisible = false; return true; }
        if (model.IsOverview) return false;
        model.Section = Section.Overview;
        return true;
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SectionIndex)) { More.IsVisible = false; MarkMore(); }
    }

    // «Ещё» lights up while one of the sections under it is open.
    private void MarkMore() => NavMore.Classes.Set("selected", main != null && !(main.IsOverview || main.IsSend || main.IsSwap || main.IsHistory));

    private void OnSection(object? sender, RoutedEventArgs e)
    {
        if (main == null || sender is not Control { Tag: string tag } || !Enum.TryParse<Section>(tag, out var section)) return;
        More.IsVisible = false;
        main.Section = section;
    }

    private void OnMore(object? sender, RoutedEventArgs e) => More.IsVisible = !More.IsVisible;

    private void OnMoreBackdrop(object? sender, PointerPressedEventArgs e) => More.IsVisible = false;
}
