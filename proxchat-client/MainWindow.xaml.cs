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
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Load configuration
        var config = LoadConfiguration();
        _viewModel = new MainViewModel(config);
        DataContext = _viewModel;

        // Attach handlers using WPF standard events
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        // Add LostFocus to cancel editing PTT key
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
        if (_viewModel.IsEditingPushToTalk)
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
            // Optional: Give focus back to main window or specific element
            Focus(); 
            return;
        }

        // Handle PTT activation
        // Check if PTT is enabled and the pressed key matches the assigned PTT key
        if (_viewModel.IsPushToTalk && e.Key == _viewModel.PushToTalkKey)
        {
            // Prevent repeated calls if key is held down
            if (!e.IsRepeat)
            {
                _viewModel.SetPushToTalkActive(true);
            }
            e.Handled = true; // Prevent further processing of this key event
        }
        else if (e.Key == _viewModel.MuteSelfKey)
        {
            _viewModel.ToggleSelfMute();
        }
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Handle PTT deactivation
        // Check if PTT is enabled and the released key matches the assigned PTT key
        if (_viewModel.IsPushToTalk && e.Key == _viewModel.PushToTalkKey)
        {
            _viewModel.SetPushToTalkActive(false);
            e.Handled = true; // Prevent further processing of this key event
        }
    }
    
    private void MainWindow_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // If the window loses focus while editing, cancel editing
        if (_viewModel.IsEditingPushToTalk)
        {
            _viewModel.IsEditingPushToTalk = false;
        }
        
        // Also ensure PTT is deactivated if focus is lost while key was held
        // (This might be redundant if PreviewKeyUp is reliable, but safer)
        if (_viewModel.IsPushToTalk) // Check if PTT enabled
        {
            // Check if the PTT key is currently down (using Keyboard.IsKeyDown)
            if(Keyboard.IsKeyDown(_viewModel.PushToTalkKey))
            {
                 _viewModel.SetPushToTalkActive(false); 
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is MainViewModel viewModel)
        {
            if (viewModel.IsPushToTalk && e.Key == viewModel.PushToTalkKey)
            {
                viewModel.SetPushToTalkActive(true);
            }
            else if (e.Key == viewModel.MuteSelfKey)
            {
                viewModel.ToggleSelfMute();
            }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (DataContext is MainViewModel viewModel)
        {
            if (viewModel.IsPushToTalk && e.Key == viewModel.PushToTalkKey)
            {
                viewModel.SetPushToTalkActive(false);
            }
        }
    }
}