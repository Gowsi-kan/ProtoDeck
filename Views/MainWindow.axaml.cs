using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using ProtoDeck.ViewModels;

namespace ProtoDeck.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                ResizeHandles.IsVisible = WindowState == WindowState.Normal;
                MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
                    ? "M7,4 H20 V17 H17 V20 H4 V7 H7 Z M8,5 V7 H17 V16 H19 V5 Z M5,8 V19 H16 V8 Z"
                    : "M5,5 H19 V19 H5 Z M6,6 V18 H18 V6 Z");
            }
        };
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            BeginMoveDrag(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Normal &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control { Tag: string tag } &&
            Enum.TryParse<WindowEdge>(tag, out var edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    public async Task<IReadOnlyList<string>> PickProtosAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select .proto files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Protobuf") { Patterns = ["*.proto"] }]
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private async void OnCopyResponse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.ResponseText);
    }
}
