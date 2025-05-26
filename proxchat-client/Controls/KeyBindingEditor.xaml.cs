using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProxChatClient.Services; // For HotkeyDefinition
using ProxChatClient.Converters;

namespace ProxChatClient.Controls;

public partial class KeyBindingEditor : UserControl
{
    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(
            nameof(Hotkey),
            typeof(HotkeyDefinition),
            typeof(KeyBindingEditor),
            new PropertyMetadata(new HotkeyDefinition(), OnHotkeyChanged));

    public static readonly DependencyProperty IsEditingProperty =
        DependencyProperty.Register(
            nameof(IsEditing),
            typeof(bool),
            typeof(KeyBindingEditor),
            new PropertyMetadata(false));

    // Backwards compatibility property - maps to Hotkey.Key
    public Key Key
    {
        get => Hotkey?.Key ?? Key.None;
        set 
        { 
            if (Hotkey == null)
                Hotkey = new HotkeyDefinition(value);
            else
                Hotkey = new HotkeyDefinition(value, Hotkey.Ctrl, Hotkey.Shift, Hotkey.Alt, Hotkey.Win);
        }
    }

    public HotkeyDefinition Hotkey
    {
        get => (HotkeyDefinition)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value ?? new HotkeyDefinition());
    }

    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public event RoutedEventHandler? KeyChanged;
    public event RoutedEventHandler? HotkeyChanged;

    private readonly KeyDisplayConverter _keyDisplayConverter = new();

    public KeyBindingEditor()
    {
        InitializeComponent();
        UpdateKeyDisplay();
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyBindingEditor editor)
        {
            editor.UpdateKeyDisplay();
        }
    }

    private void UpdateKeyDisplay()
    {
        if (Hotkey?.Key == Key.None || Hotkey == null)
        {
            KeyDisplay.Text = "None";
        }
        else
        {
            // use converter to get better display text
            var displayText = _keyDisplayConverter.Convert(Hotkey.ToString(), typeof(string), parameter: null!, System.Globalization.CultureInfo.CurrentCulture);
            KeyDisplay.Text = displayText?.ToString() ?? Hotkey.ToString();
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsEditing)
        {
            // Cancel editing
            IsEditing = false;
            EditButton.Content = "Edit";
            UpdateKeyDisplay();
        }
        else
        {
            // Start editing
            IsEditing = true;
            EditButton.Content = "Cancel";
            KeyDisplay.Text = "Press key combination...";
            Focus();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsEditing)
        {
            e.Handled = true;

            // Don't allow modifier keys alone
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                e.Key == Key.LWin || e.Key == Key.RWin)
            {
                // Just update display to show current modifiers being held
                var currentModifiers = Keyboard.Modifiers;
                var previewText = new System.Text.StringBuilder();
                
                if (currentModifiers.HasFlag(ModifierKeys.Control))
                    previewText.Append("Ctrl + ");
                if (currentModifiers.HasFlag(ModifierKeys.Shift))
                    previewText.Append("Shift + ");
                if (currentModifiers.HasFlag(ModifierKeys.Alt))
                    previewText.Append("Alt + ");
                if (currentModifiers.HasFlag(ModifierKeys.Windows))
                    previewText.Append("Win + ");
                
                previewText.Append("?");
                KeyDisplay.Text = previewText.ToString();
                return;
            }

            // Capture the current modifier state
            var modifiers = Keyboard.Modifiers;
            
            // Create new hotkey definition
            var newHotkey = new HotkeyDefinition(
                e.Key,
                modifiers.HasFlag(ModifierKeys.Control),
                modifiers.HasFlag(ModifierKeys.Shift),
                modifiers.HasFlag(ModifierKeys.Alt),
                modifiers.HasFlag(ModifierKeys.Windows)
            );

            // Update the hotkey
            Hotkey = newHotkey;
            IsEditing = false;
            EditButton.Content = "Edit";
            UpdateKeyDisplay();

            // Raise both events for backwards compatibility
            KeyChanged?.Invoke(this, new RoutedEventArgs());
            HotkeyChanged?.Invoke(this, new RoutedEventArgs());
        }
        else
        {
            base.OnPreviewKeyDown(e);
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (IsEditing)
        {
            IsEditing = false;
            EditButton.Content = "Edit";
            UpdateKeyDisplay();
        }
    }
} 