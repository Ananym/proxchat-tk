using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ProxChatClient.Models;
using ProxChatClient.ViewModels;
using ProxChatClient.Controls;

namespace ProxChatClient;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Load configuration
        var config = LoadConfiguration();
        _viewModel = new MainViewModel(config);
        DataContext = _viewModel;

        // Attach handlers using WPF standard events (keep for UI editing)
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        LostKeyboardFocus += MainWindow_LostKeyboardFocus;
    }

    private Config LoadConfiguration()
    {
        try
        {
            var json = File.ReadAllText("config.json");
            return JsonSerializer.Deserialize<Config>(json) ?? new Config();
        }
        catch
        {
            return new Config();
        }
    }

    private void PushToTalkKeyEditor_KeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is KeyBindingEditor editor)
        {
            viewModel.PushToTalkKey = editor.Key;
        }
    }

    private void MuteSelfKeyEditor_KeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is KeyBindingEditor editor)
        {
            viewModel.MuteSelfKey = editor.Key;
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Handle editing PTT Key
        if (_viewModel?.IsEditingPushToTalk == true)
        {
             // Ignore modifier keys
             if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl || 
                 e.Key == Key.LeftShift || e.Key == Key.RightShift || 
                 e.Key == Key.LeftAlt || e.Key == Key.RightAlt || 
                 e.Key == Key.LWin || e.Key == Key.RWin ||
                 e.Key == Key.System || e.Key == Key.Capital || 
                 e.Key == Key.NumLock || e.Key == Key.Scroll ||
                 e.Key == Key.Snapshot || e.Key == Key.Apps)
             {
                 return;
             }
            
            // Assign the key
            _viewModel.PushToTalkKey = e.Key; 
            _viewModel.IsEditingPushToTalk = false; // Stop editing mode
            e.Handled = true;
            Focus(); 
            return;
        }
        
        // Handle editing Mute Self Key
        if (_viewModel?.IsEditingMuteSelf == true)
        {
             // Ignore modifier keys
             if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl || 
                 e.Key == Key.LeftShift || e.Key == Key.RightShift || 
                 e.Key == Key.LeftAlt || e.Key == Key.RightAlt || 
                 e.Key == Key.LWin || e.Key == Key.RWin ||
                 e.Key == Key.System || e.Key == Key.Capital || 
                 e.Key == Key.NumLock || e.Key == Key.Scroll ||
                 e.Key == Key.Snapshot || e.Key == Key.Apps)
             {
                 return;
             }
            
            // Assign the key
            _viewModel.MuteSelfKey = e.Key; 
            _viewModel.IsEditingMuteSelf = false; // Stop editing mode
            e.Handled = true;
            Focus(); 
            return;
        }
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        // No longer needed for global hotkeys - handled by GlobalHotkeyService
    }
    
    private void MainWindow_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // If the window loses focus while editing, cancel editing
        if (_viewModel?.IsEditingPushToTalk == true)
        {
            _viewModel.IsEditingPushToTalk = false;
        }
        
        if (_viewModel?.IsEditingMuteSelf == true)
        {
            _viewModel.IsEditingMuteSelf = false;
        }
    }
}