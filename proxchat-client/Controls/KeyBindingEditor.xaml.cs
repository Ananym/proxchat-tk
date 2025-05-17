using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProxChatClient.Controls;

public partial class KeyBindingEditor : UserControl
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.Register(
            nameof(Key),
            typeof(Key),
            typeof(KeyBindingEditor),
            new PropertyMetadata(Key.None, OnKeyChanged));

    public static readonly DependencyProperty IsEditingProperty =
        DependencyProperty.Register(
            nameof(IsEditing),
            typeof(bool),
            typeof(KeyBindingEditor),
            new PropertyMetadata(false));

    public Key Key
    {
        get => (Key)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public event RoutedEventHandler? KeyChanged;

    public KeyBindingEditor()
    {
        InitializeComponent();
        UpdateKeyDisplay();
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyBindingEditor editor)
        {
            editor.UpdateKeyDisplay();
        }
    }

    private void UpdateKeyDisplay()
    {
        if (Key == Key.None)
        {
            KeyDisplay.Text = "None";
        }
        else
        {
            var modifiers = Keyboard.Modifiers;
            var keyText = new System.Text.StringBuilder();

            if (modifiers.HasFlag(ModifierKeys.Control))
                keyText.Append("Ctrl + ");
            if (modifiers.HasFlag(ModifierKeys.Alt))
                keyText.Append("Alt + ");
            if (modifiers.HasFlag(ModifierKeys.Shift))
                keyText.Append("Shift + ");
            if (modifiers.HasFlag(ModifierKeys.Windows))
                keyText.Append("Win + ");

            keyText.Append(Key.ToString());
            KeyDisplay.Text = keyText.ToString();
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
            KeyDisplay.Text = "Press any key...";
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
                return;
            }

            // Update the key
            Key = e.Key;
            IsEditing = false;
            EditButton.Content = "Edit";
            UpdateKeyDisplay();

            // Raise the KeyChanged event
            KeyChanged?.Invoke(this, new RoutedEventArgs());
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