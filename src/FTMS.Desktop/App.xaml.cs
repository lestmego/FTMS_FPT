using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using FTMS.Infrastructure;

namespace FTMS.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "error.log"),
            $"{DateTimeOffset.Now:O} {TelegramErrorSanitizer.Sanitize(e.Exception.ToString())}\n\n");
        System.Windows.MessageBox.Show($"FTMS Companion g\u1eb7p l\u1ed7i: {TelegramErrorSanitizer.Sanitize(e.Exception.Message)}",
            "FTMS Companion", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

