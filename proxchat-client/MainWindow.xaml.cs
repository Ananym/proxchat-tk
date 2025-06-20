using System.Windows;
using System.Windows.Input;
using ProxChatClient.ViewModels;
using ProxChatClient.Controls;

namespace ProxChatClient;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Attach handlers using WPF standard events (keep for UI editing)
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        LostKeyboardFocus += MainWindow_LostKeyboardFocus;
    }

    private void PushToTalkKeyEditor_HotkeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is KeyBindingEditor editor)
        {
            viewModel.PushToTalkHotkey = editor.Hotkey;
        }
    }

    private void MuteSelfKeyEditor_HotkeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is KeyBindingEditor editor)
        {
            viewModel.MuteSelfHotkey = editor.Hotkey;
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Key capture is now handled by the KeyBindingEditor controls
        // This method can be kept for any future window-level key handling
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        // No longer needed for global hotkeys - handled by GlobalHotkeyService
    }
    
    private void MainWindow_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Focus loss is handled by individual KeyBindingEditor controls
    }
}