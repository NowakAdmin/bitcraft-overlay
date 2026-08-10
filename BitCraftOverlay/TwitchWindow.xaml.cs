using System.Windows;

namespace BitCraftOverlay;

// Normal resizable window (not part of the borderless overlay) - the user watches
// a real stream here and logs into their Twitch account for drops, so standard
// window chrome (resize, minimize, maximize) is what they actually want.
public partial class TwitchWindow : Window
{
    private bool _muted;

    public TwitchWindow() => InitializeComponent();

    // CoreWebView2.IsMuted mutes the whole embedded browser at the OS/media level,
    // independent of Twitch's own in-page mute button.
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return; // still starting up, ignore this click
        _muted = !_muted;
        Browser.CoreWebView2.IsMuted = _muted;
        MuteButton.Content = _muted ? "🔇 Wyciszone" : "🔊 Wycisz";
    }
}
