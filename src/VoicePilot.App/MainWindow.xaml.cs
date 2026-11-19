using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VoicePilot.App.ViewModels;

namespace VoicePilot.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ExtendsContentIntoTitleBar = false;
        SystemBackdrop = new MicaBackdrop();
        Title = "VoicePilot · 实时语音翻译";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1000;
            presenter.PreferredMinimumHeight = 720;
        }

        AppWindow.Resize(new SizeInt32(1280, 800));
        RootGrid.Loaded += OnRootLoaded;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        RootGrid.Loaded -= OnRootLoaded;
        await ViewModel.InitializeAsync();
    }

    public MainViewModel ViewModel { get; }
}
