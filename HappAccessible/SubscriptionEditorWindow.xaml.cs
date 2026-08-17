using System.Windows;

namespace HappAccessible;

public partial class SubscriptionEditorWindow : Window
{
    public string SubscriptionText { get; private set; }

    public SubscriptionEditorWindow(string current)
    {
        SubscriptionText = current ?? "";
        InitializeComponent();
        SubscriptionBox.Text = SubscriptionText;
        Loaded += (_, _) =>
        {
            SubscriptionBox.Focus();
            SubscriptionBox.CaretIndex = SubscriptionBox.Text?.Length ?? 0;
        };
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        SubscriptionText = SubscriptionBox.Text ?? "";
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
