using System.Windows;

namespace FTMS.Desktop;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    public SettingsWindow(SettingsStore store)
    {
        InitializeComponent(); _store = store;
        TokenBox.Password = store.Current.TelegramToken; ChatIdBox.Text = store.Current.TelegramChatId; StartupCheck.IsChecked = store.Current.StartWithWindows;
        AutoRefreshCheck.IsChecked = store.Current.AutoRefreshEnabled; RefreshSecondsBox.Text = store.Current.AutoRefreshSeconds.ToString();
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        var seconds = int.TryParse(RefreshSecondsBox.Text, out var value) ? Math.Clamp(value, 5, 3600) : 30;
        _store.Save(new DesktopSettings(TokenBox.Password.Trim(), ChatIdBox.Text.Trim(), StartupCheck.IsChecked == true,
            AutoRefreshCheck.IsChecked == true, seconds));
        DialogResult = true;
    }
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
