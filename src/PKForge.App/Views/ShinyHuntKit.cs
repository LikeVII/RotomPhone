using System.Collections.Concurrent;
using System.Globalization;
using PKForge.App.Services;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>Colours and small builders shared by the shiny hunting screens (a temporary look, copied from the reference site).</summary>
public static class ShinyUi
{
    public static readonly Color Background = Color.FromArgb("#17181c");
    public static readonly Color Bar = Color.FromArgb("#232428");
    public static readonly Color Card = Color.FromArgb("#2b2c30");
    public static readonly Color Panel = Color.FromArgb("#232428");
    public static readonly Color Field = Color.FromArgb("#1a1b1f");
    public static readonly Color Accent = Color.FromArgb("#5c6bc0");
    public static readonly Color AccentText = Color.FromArgb("#8e9ae6");
    public static readonly Color Muted = Color.FromArgb("#a4a8b8");
    public static readonly Color Gold = Color.FromArgb("#d4b53a");
    public static readonly Color Danger = Color.FromArgb("#e57373");

    public const string FontName = "RotomUI";

    public static Label Txt(string text, double size = 16, Color? color = null, bool bold = false, TextAlignment align = TextAlignment.Start) => new()
    {
        Text = text,
        FontFamily = FontName,
        FontSize = size,
        TextColor = color ?? Colors.White,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        HorizontalTextAlignment = align,
        VerticalTextAlignment = TextAlignment.Center,
    };

    /// <summary>A round or pill button with a glyph or short text.</summary>
    public static Button Round(string text, double size = 46, bool filled = false, Color? color = null) => new()
    {
        Text = text,
        FontSize = size * 0.42,
        TextColor = color ?? (filled ? Colors.White : AccentText),
        BackgroundColor = filled ? Accent : Colors.Transparent,
        BorderColor = Accent,
        BorderWidth = filled ? 0 : 2,
        CornerRadius = (int)(size / 2),
        WidthRequest = size,
        HeightRequest = size,
        Padding = 0,
    };

    /// <summary>A wide dark button, as the site draws its settings actions.</summary>
    public static Button Wide(string text) => new()
    {
        Text = text,
        FontFamily = FontName,
        FontSize = 17,
        TextColor = Colors.White,
        BackgroundColor = Field,
        CornerRadius = 10,
        HeightRequest = 50,
    };

    public static BoxView Line() => new() { HeightRequest = 2, Color = Accent, Opacity = 0.7 };

    /// <summary>"2h, 44m, 52s", as the site writes times.</summary>
    public static string Duration(long milliseconds)
    {
        var total = Math.Max(0, milliseconds) / 1000;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours}h");
        if (hours > 0 || minutes > 0) parts.Add($"{minutes}m");
        parts.Add($"{seconds}s");
        return string.Join(", ", parts);
    }

    /// <summary>"1:02:03", "2:03" or "3" to milliseconds; null when it is not a time.</summary>
    public static long? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        long total = 0;
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total * 1000;
    }

    /// <summary>"3:07:59", the form the time prompts show.</summary>
    public static string Clock(long milliseconds)
    {
        var total = Math.Max(0, milliseconds) / 1000;
        return $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}";
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string SpeciesName(IGameDataService data, int species) =>
        (uint)species < (uint)data.SpeciesNames.Count && data.SpeciesNames[species].Length > 0 ? data.SpeciesNames[species] : $"#{species}";

    public static string BallName(IGameDataService data, int ball) =>
        (uint)ball < (uint)data.BallNames.Count && data.BallNames[ball].Length > 0 ? data.BallNames[ball] : "Poké Ball";
}

/// <summary>
/// An image of a Pokémon (shiny HOME render, or the pixel sprite when HOME has none) or of a ball.
/// Pictures arrive from the sprite cache or the network, so the image fills itself in when ready.
/// </summary>
public sealed class SpriteImage : Image
{
    private static readonly ConcurrentDictionary<string, byte[]> Encoded = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> Order = new();
    private const int MaxEncoded = 160;

    private string? _wanted;

    public SpriteImage()
    {
        Aspect = Aspect.AspectFit;
    }

    /// <summary>Shows the shiny render of a species.</summary>
    public void ShowShiny(ISpriteService sprites, int species) => ShowLook(sprites, new SpriteLook(species, 0, true));

    public void ShowLook(ISpriteService sprites, SpriteLook look)
    {
        _wanted = "mon-" + look.CacheKey;
        Source = null;
        TryMon(sprites, look, _wanted, 0);
    }

    public void ShowBall(ISpriteService sprites, int ball)
    {
        _wanted = $"ball-{ball}";
        Source = null;
        TryBall(sprites, ball, _wanted);
    }

    public void Clear()
    {
        _wanted = null;
        Source = null;
    }

    private void TryMon(ISpriteService sprites, SpriteLook look, string key, int attempt)
    {
        if (_wanted != key) return;
        if (Encoded.TryGetValue(key, out var known)) { Apply(known); return; }
        var bitmap = sprites.GetHome(look);
        if (bitmap is null && sprites.HomeUnavailable(look)) bitmap = sprites.GetSprite(look);
        if (bitmap is not null) { Store(key, bitmap); return; }
        if (attempt >= 3) return;
        void Again() => MainThread.BeginInvokeOnMainThread(() => TryMon(sprites, look, key, attempt + 1));
        if (sprites.HomeUnavailable(look)) sprites.Warm(look, Again);
        else sprites.WarmHome(look, Again);
    }

    private void TryBall(ISpriteService sprites, int ball, string key)
    {
        if (_wanted != key) return;
        if (Encoded.TryGetValue(key, out var known)) { Apply(known); return; }
        var bitmap = sprites.GetBall(ball);
        if (bitmap is not null) { Store(key, bitmap); return; }
        sprites.WarmBall(ball, () => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_wanted != key) return;
            var loaded = sprites.GetBall(ball);
            if (loaded is not null) Store(key, loaded);
        }));
    }

    private void Store(string key, SKBitmap bitmap)
    {
        Task.Run(() =>
        {
            byte[]? bytes = null;
            try
            {
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                bytes = data?.ToArray();
            }
            catch (Exception error)
            {
                AppLog.Warn("shiny", $"Could not encode a picture: {error.Message}");
            }
            if (bytes is null) return;
            Encoded[key] = bytes;
            Order.Enqueue(key);
            while (Order.Count > MaxEncoded && Order.TryDequeue(out var old)) Encoded.TryRemove(old, out _);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_wanted == key) Apply(bytes);
            });
        });
    }

    private void Apply(byte[] bytes) => Source = ImageSource.FromStream(() => new MemoryStream(bytes));
}
