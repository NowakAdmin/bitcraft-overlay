using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BitCraftOverlay;

/// <summary>
/// BitCraft's 10-tier color palette, lifted from brico.app's open-source dark-mode tier
/// colors (github.com/BitCraftToolBox/brico) so the overlay matches the game's own tier badges.
/// </summary>
internal static class TierPalette
{
    private static readonly string[] Hex =
    {
        "#636A74", "#875F45", "#5C6F4D", "#49619C", "#814F87",
        "#983A44", "#947014", "#538484", "#464953", "#97AFBE",
    };

    public static readonly Brush[] Brushes =
        Hex.Select(h => (Brush)new SolidColorBrush((Color)ColorConverter.ConvertFromString(h))).ToArray();
}

/// <summary>
/// Skill level (1-100+) -> the tier color for that level bracket (1-19 = tier 1, then a clean
/// 10-level step per tier: 20-29 = tier 2, ..., 90-99 = tier 9, 100+ = tier 10).
/// </summary>
public class LevelTierBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int level || level <= 0) return Brushes.Transparent;
        // Tier 1 covers a wider starter band (1-19); every tier after that is a clean 10-level
        // step starting at 20 (20-29 = tier 2, ..., 60-69 = tier 6, etc.) - confirmed by matching
        // the reported colors (tier 1 gray, tier 2 brown, tier 6 red) against real level brackets.
        var tierIndex = level < 20 ? 0 : Math.Min(1 + (level - 20) / 10, TierPalette.Brushes.Length - 1);
        return TierPalette.Brushes[tierIndex];
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Item tier (1-10, as reported by the API) -> that tier's color directly, no bracket math.</summary>
public class ItemTierBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int tier || tier <= 0) return Brushes.Transparent;
        return TierPalette.Brushes[Math.Min(tier - 1, TierPalette.Brushes.Length - 1)];
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
