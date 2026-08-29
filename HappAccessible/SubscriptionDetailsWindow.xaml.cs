using System.Windows;

namespace HappAccessible;

public partial class SubscriptionDetailsWindow : Window
{
    public SubscriptionDetailsWindow(
        string title,
        string source,
        string servers,
        string used,
        string remaining,
        string expiry,
        string updated,
        string interval,
        string state,
        string support)
    {
        InitializeComponent();
        TitleText.Text = title;
        SourceText.Text = source;
        ServersText.Text = servers;
        UsedText.Text = used;
        RemainingText.Text = remaining;
        ExpiryText.Text = expiry;
        UpdatedText.Text = updated;
        IntervalText.Text = interval;
        StateText.Text = state;
        SupportText.Text = support;
        StatusText.Text = "Сведения получены из последнего ответа сервера подписки. " +
                          "Секретная ссылка здесь не показывается.";
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
