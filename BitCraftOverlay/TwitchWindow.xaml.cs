using System.Windows;

namespace BitCraftOverlay;

// Normal resizable window (not part of the borderless overlay) - the user watches
// a real stream here and logs into their Twitch account for drops, so standard
// window chrome (resize, minimize, maximize) is what they actually want.
public partial class TwitchWindow : Window
{
    public TwitchWindow() => InitializeComponent();
}
