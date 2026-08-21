using System.Configuration;
using System.Data;
using System.Windows;

namespace BitCraftOverlay;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // Safety net: an unhandled exception on the UI thread otherwise kills the
        // whole process with no message (that's what happened before this existed).
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show($"An error occurred:\n{e.Exception.Message}", "BitCraft Overlay",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}

