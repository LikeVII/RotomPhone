using Plugin.Maui.Rive;
using PKForge.App.Services;
using PKForge.Domain;
using PKForge.Engine;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Rotom Phone home screen, built from the Figma frame "01 Home" (922 x 2048 design units).
/// Bottom to top: animated Rive background, the floor and the Pokémon (anchored to the bottom),
/// then Rotom with its blinking eyes, speaking mouth, info screen and the yellow box (anchored to the top).
/// </summary>
public sealed class RotomHomePage : ContentPage
{
    private const float DesignW = 922f, DesignH = 2048f;
    private const int BubbleMs = 15000;
    private const int HoldMs = 550;
    private const float BackgroundSpeed = 0.6f;
    private const float PokemonPad = 70f;

    private static readonly SKRect RotomRect = new(-97f, 143.5f, -97f + 1116.5f, 143.5f + 383.5f);
    private static readonly SKRect PokemonBox = new(-35f, 727f, -35f + 973f, 727f + 973f);
    private static readonly SKColor InkGrey = SKColor.Parse("#7a7b76");

    private enum Target { None, Handle, LeftFlap, Profile, NextMon, Pokemon, MenuItem }

    /// <summary>One button of the menu: its picture, the text drawn on it (blank buttons only) and what it does (null = nothing yet).</summary>
    private sealed record MenuItem(string Asset, string[]? Label, Func<Task>? Action);

    // The menu frame (1080 wide) is slightly wider than the home frame (922 wide). The Rotom keeps its size;
    // the panel body and the buttons are scaled by MenuK to fit the width, the part behind the Rotom is not.
    private const float MenuK = 922f / 1080f;
    private static float MenuX(float x) => 461f + (x - 540f) * MenuK;
    private static float MenuY(float y) => 506f + (y - 506f) * MenuK;
    private static readonly SKRect MenuRect = new(-82f, 316f, 1010f, 506f + 1894f * MenuK + 1f);

    private sealed record BubbleInfo(string Number, string Name, IReadOnlyList<int> Types, IReadOnlyList<string> Lines, int Ball);

    private readonly IBankService _bank;
    private readonly ISpriteService _sprites;
    private readonly TrainerProfileStore _profiles;
    private readonly Grid _root;
    private readonly SKCanvasView _top;
    private readonly SKCanvasView _bottom;
    private RiveAnimationView? _rive;
    private readonly MenuItem[] _items;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Random _random = new();
    private IDispatcherTimer? _timer;

    private BankEntry? _entry;
    private BubbleInfo? _bubble;
    private long _bubbleUntil;
    private float _bubbleAlpha;
    private bool _mouthOpen;
    private long _nextMouthToggle;
    private bool _eyesClosed;
    private long _nextBlink;
    private long _eyesReopen;
    private int _statIndex;
    private long _nextStat;
    private string[] _stats = ["Banque vide"];
    private string _userName = "Dresseur";

    private SKBitmap? _homeBitmap;
    private SKImage? _homeImage;
    private bool _menuOpen;
    private bool _navigating;
    private float _menuProgress;
    private int _pressItem = -1;
    private SKImage? _menuCache, _squareCache, _floorCache;
    private string _menuKey = "", _squareKey = "", _floorKey = "";

    // Touch state
    private Target _pressTarget;
    private SKPoint _pressPoint;
    private bool _holdFired;
    private bool _moved;
    private bool _menuTriggered;
    private IDispatcherTimer? _holdTimer;

    private float _scale = 1f, _offX, _botY, _designLeft, _designRight = DesignW;

    private SKImage? _pokeCache;
    private SKBitmap? _pokeCacheSource;
    private float _pokeCacheScale;
    private SKBitmap? _ballBitmap;
    private SKImage? _ballImage;

    public RotomHomePage(IBankService bank, ISpriteService sprites, TrainerProfileStore profiles)
    {
        _bank = bank;
        _sprites = sprites;
        _profiles = profiles;
        BackgroundColor = Color.FromArgb("#9ADDDA");
        NavigationPage.SetHasNavigationBar(this, false);

        _root = new Grid();
        _bottom = new SKCanvasView { InputTransparent = true };
        _bottom.PaintSurface += OnPaintBottom;
        _root.Add(_bottom);
        _top = new SKCanvasView { EnableTouchEvents = true };
        _top.PaintSurface += OnPaintTop;
        _top.Touch += OnTouch;
        _root.Add(_top);

        _items =
        [
            new("btn_pokedex", null, null),
            new("btn_pokemon", null, () => OpenAsync<BankPage>()),
            new("btn_shiny_collection", null, null),
            new("btn_teams", null, null),
            new("btn_blank", ["Poképark"], () => OpenOldHomeAsync(HomeStartAction.Park)),
            new("btn_blank", ["Autopilote"], () => OpenOldHomeAsync(HomeStartAction.Autopilot)),
            new("btn_blank", ["Événements"], () => OpenOldHomeAsync(HomeStartAction.Events)),
            new("btn_blank", ["Réglages"], () => OpenOldHomeAsync(HomeStartAction.Settings)),
            new("btn_blank", ["Jeux et", "sauvegardes"], () => OpenOldHomeAsync(HomeStartAction.None)),
            new("btn_blank", ["Restauration"], () => OpenAsync<BackupHistoryPage>()),
        ];
        Content = _root;
        _ = RotomAssets.WarmAsync().ContinueWith(_ => MainThread.BeginInvokeOnMainThread(InvalidateAll));
        _ = RotomFont.WarmAsync().ContinueWith(_ => MainThread.BeginInvokeOnMainThread(InvalidateAll));
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AttachRive();
        RefreshFromBank();
        var now = _clock.ElapsedMilliseconds;
        _nextBlink = now + 2500;
        _nextStat = now + 3500;
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void InvalidateTop() => _top.InvalidateSurface();

    private void InvalidateAll()
    {
        _bottom.InvalidateSurface();
        _top.InvalidateSurface();
    }

    /// <summary>
    /// The animated background lives only while this page is on screen. Android destroys the page's
    /// views while another page covers it, and a Rive view brought back from that state crashes the
    /// app, so the view is built fresh every time the page appears.
    /// </summary>
    private void AttachRive()
    {
        if (_rive is not null) return;
        // Preview-quality plugin: a failure just leaves the plain teal colour.
        try
        {
            var rive = new RiveAnimationView
            {
                ResourceName = "home_background",
                AutoPlay = true,
                Fit = RiveFitMode.Cover,
                RiveAlignment = RiveAlignmentMode.Center,
                InputTransparent = true,
            };
            _root.Insert(0, rive);
            _rive = rive;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), ApplyBackgroundSpeed);
        }
        catch (Exception error)
        {
            Services.AppLog.Warn("rotom", $"Rive background unavailable: {error.Message}");
        }
    }

    private void DetachRive()
    {
        var rive = _rive;
        _rive = null;
        if (rive is null) return;
        try { _root.Remove(rive); }
        catch (Exception error) { Services.AppLog.Warn("rotom", $"Rive background removal failed: {error.Message}"); }
    }

    private void ApplyBackgroundSpeed()
    {
#if ANDROID
        try
        {
            // The plugin does not expose playback speed, so it is reached through Java reflection:
            // view.getController().setSpeed(speed). Any failure just leaves the normal speed.
            if (_rive?.Handler?.PlatformView is Java.Lang.Object native)
            {
                var controller = native.Class.GetMethod("getController", Array.Empty<Java.Lang.Class>())?.Invoke(native);
                controller?.Class.GetMethod("setSpeed", Java.Lang.Float.Type!)?.Invoke(controller, Java.Lang.Float.ValueOf(BackgroundSpeed));
            }
        }
        catch (Exception error)
        {
            Services.AppLog.Warn("rotom", $"Could not slow the background: {error.Message}");
        }
#endif
    }

    protected override void OnDisappearing()
    {
        DetachRive();
        _timer?.Stop();
        _holdTimer?.Stop();
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
        if (_menuOpen) { _ = CloseMenuAsync(); return true; }
        return base.OnBackButtonPressed();
    }

    /// <summary>Picks a random Pokémon of the Bank and recounts the achievements.</summary>
    private void RefreshFromBank()
    {
        var all = _bank.GetAll();
        _userName = _profiles.Profiles.FirstOrDefault()?.DisplayName is { Length: > 0 } n ? n : "Dresseur";
        if (all.Count == 0)
        {
            _entry = null;
            _bubble = null;
            _stats = ["Banque vide"];
        }
        else
        {
            if (_entry is null || all.All(e => e.Id != _entry.Id))
            {
                _entry = all[_random.Next(all.Count)];
                _bubble = null;
            }
            _stats =
            [
                $"Pokémon : {all.Count}",
                $"Chromatiques : {all.Count(e => e.Info.Shiny)}",
                $"Espèces : {all.Select(e => e.Info.Species).Distinct().Count()}",
                $"Boîtes : {_bank.BoxCount}",
            ];
        }
        _statIndex = 0;
        InvalidateAll();
    }

    // ── Animation clock ──────────────────────────────────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.ElapsedMilliseconds;
        var dirty = false;

        // Blinking: eyes closed for a moment every few seconds.
        if (_eyesClosed && now >= _eyesReopen) { _eyesClosed = false; _nextBlink = now + 2500 + _random.Next(3000); dirty = true; }
        else if (!_eyesClosed && now >= _nextBlink) { _eyesClosed = true; _eyesReopen = now + 140; dirty = true; }

        // The yellow box: speaking mouth while it is shown, then it goes away after 15 s.
        var bubbleShown = _bubbleUntil > now;
        if (bubbleShown)
        {
            if (now >= _nextMouthToggle) { _mouthOpen = !_mouthOpen; _nextMouthToggle = now + 150; dirty = true; }
        }
        else if (_mouthOpen) { _mouthOpen = false; dirty = true; }
        var targetAlpha = bubbleShown ? 1f : 0f;
        if (Math.Abs(_bubbleAlpha - targetAlpha) > 0.001f)
        {
            _bubbleAlpha = targetAlpha > _bubbleAlpha ? Math.Min(1f, _bubbleAlpha + 0.15f) : Math.Max(0f, _bubbleAlpha - 0.1f);
            dirty = true;
        }

        // Achievements change in turn.
        if (now >= _nextStat) { _statIndex = (_statIndex + 1) % Math.Max(1, _stats.Length); _nextStat = now + 3500; dirty = true; }

        if (dirty) InvalidateTop();
    }

    // ── Painting ─────────────────────────────────────────────────────────────

    private bool ComputeLayout(SKImageInfo info)
    {
        float w = info.Width, h = info.Height;
        if (w <= 0 || h <= 0) return false;
        _scale = Math.Min(w / DesignW, h / DesignH);
        _offX = (w - DesignW * _scale) / 2f;
        _botY = h - DesignH * _scale;
        _designLeft = -_offX / _scale;
        _designRight = DesignW + _offX / _scale;
        return true;
    }

    /// <summary>
    /// Bottom layer: the floor, the Pokémon, the dark square and the menu panel (which slides out from
    /// under the Rotom). Repainted only when one of them changes.
    /// </summary>
    private void OnPaintBottom(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        if (!ComputeLayout(e.Info)) return;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var p = _menuProgress;

        if (p < 0.999f)
        {
            c.Save();
            c.Translate(_offX, _botY);
            c.Scale(_scale);
            // The floor reaches both screen edges, however wide the phone is.
            var floorRect = new SKRect(_designLeft, 1396, _designRight, 2048);
            var floor = Cached(ref _floorCache, ref _floorKey, floorRect, ["rotomphone/rotom/floor.png"],
                sc => DrawImage(sc, "rotomphone/rotom/floor.png", floorRect));
            if (floor is not null) c.DrawImage(floor, floorRect, sampling);
            DrawPokemon(c);
            c.Restore();
        }

        c.Save();
        c.Translate(_offX, 0);
        c.Scale(_scale);
        // The dark layer is always there, behind Rotom: it dims the Pokémon, never the Rotom shape.
        var squareRect = new SKRect(_designLeft, 0, _designRight, 390);
        var square = Cached(ref _squareCache, ref _squareKey, squareRect, ["rotomphone/rotom/black_square.png"],
            sc => DrawImage(sc, "rotomphone/rotom/black_square.png", squareRect));
        if (square is not null) c.DrawImage(square, squareRect, sampling);

        if (p > 0.001f)
        {
            var panel = EnsureMenuCache();
            if (panel is not null)
            {
                // The panel comes out from under the Rotom base: nothing of it shows above its top edge.
                c.Save();
                c.ClipRect(new SKRect(_designLeft - 200f, MenuRect.Top, _designRight + 200f, 6000f));
                var dy = -(1f - p) * MenuRect.Height;
                c.DrawImage(panel, new SKRect(MenuRect.Left, MenuRect.Top + dy, MenuRect.Right, MenuRect.Bottom + dy), sampling);
                if (_pressItem >= 0 && p >= 0.99f)
                {
                    var r = ItemRect(_pressItem);
                    r.Inflate(-14f * MenuK, -14f * MenuK);
                    using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true };
                    c.DrawRoundRect(r, 44f * MenuK, 44f * MenuK, shade);
                }
                c.Restore();
            }
        }
        c.Restore();
    }

    /// <summary>Top layer: Rotom and the yellow box. Repainted by the blink, mouth and fade animations.</summary>
    private void OnPaintTop(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        if (!ComputeLayout(e.Info)) return;
        c.Save();
        c.Translate(_offX, 0);
        c.Scale(_scale);
        // The Rotom shape is a big picture: it is scaled once into a cache, so each animation frame only copies it.
        var baseRect = new SKRect(RotomRect.Left, 0, RotomRect.Right, RotomRect.Bottom);
        var layer = Cached(ref _baseCache, ref _baseKey, baseRect, ["rotomphone/rotom/rotom_base.png"],
            sc => DrawImage(sc, "rotomphone/rotom/rotom_base.png", RotomRect));
        if (layer is not null)
            c.DrawImage(layer, baseRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        DrawRotom(c);
        if (_menuProgress < 0.001f) DrawBubble(c);
        c.Restore();
    }

    // ── The menu picture ─────────────────────────────────────────────────────

    private static SKRect ItemRect(int index)
    {
        var bx = 46f + 510f * (index % 2);
        var by = 604f + 341f * (index / 2);
        return new SKRect(MenuX(bx), MenuY(by), MenuX(bx + 493f), MenuY(by + 313f));
    }

    /// <summary>The red panel with its buttons, drawn once at the current size and then only moved.</summary>
    private SKImage? EnsureMenuCache()
    {
        var wanted = $"{_scale:F4}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(RotomFont.Face)}";
        if (_menuCache is not null && _menuKey == wanted) return _menuCache;
        var panel = RotomAssets.Get("rotomphone/menu/menu_panel.png");
        if (panel is null) return null;
        foreach (var item in _items)
            if (RotomAssets.Get($"rotomphone/menu/{item.Asset}.png") is null) return null;

        _menuCache?.Dispose();
        _menuCache = null;
        var width = (int)MathF.Ceiling(MenuRect.Width * _scale);
        var height = (int)MathF.Ceiling(MenuRect.Height * _scale);
        if (width <= 0 || height <= 0) return null;
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null) return null;
        var sc = surface.Canvas;
        sc.Clear(SKColors.Transparent);
        sc.Scale(width / MenuRect.Width, height / MenuRect.Height);
        sc.Translate(-MenuRect.Left, -MenuRect.Top);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var paint = new SKPaint { IsAntialias = true };

        // The body (scaled to the width of the screen), then the part that sits behind the Rotom (not scaled) on top of its first row.
        sc.DrawImage(panel, new SKRect(0, 189, panel.Width, 2084),
            new SKRect(MenuX(-3f), 506f - MenuK, MenuX(panel.Width - 3f), 506f + 1894f * MenuK), sampling, paint);
        sc.DrawImage(panel, new SKRect(0, 0, panel.Width, 190), new SKRect(-82f, 316f, -82f + panel.Width, 506f), sampling, paint);

        for (var i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            var picture = RotomAssets.Get($"rotomphone/menu/{item.Asset}.png");
            var rect = ItemRect(i);
            if (picture is not null) sc.DrawImage(picture, rect, sampling, paint);
            if (item.Label is { } lines) DrawButtonLabel(sc, rect, lines);
        }
        _menuCache = surface.Snapshot();
        _menuKey = wanted;
        return _menuCache;
    }

    /// <summary>White text with a black outline, centred on a blank button (button units: 493 x 313).</summary>
    private static void DrawButtonLabel(SKCanvas c, SKRect rect, string[] lines)
    {
        c.Save();
        c.Translate(rect.Left, rect.Top);
        c.Scale(MenuK);
        var size = lines.Length > 1 ? 60f : 72f;
        var gap = lines.Length > 1 ? 72f : 0f;
        for (var i = 0; i < lines.Length; i++)
        {
            using var probe = TextFont(size);
            var text = FitCompute(probe, lines[i], 400f, size, 36f, out var fitted);
            using var font = TextFont(fitted);
            var m = font.Metrics;
            var centre = 160f + (i - (lines.Length - 1) / 2f) * gap;
            var baseline = centre - (m.Ascent + m.Descent) / 2f;
            using var outline = new SKPaint
            {
                Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 12f, StrokeJoin = SKStrokeJoin.Round,
            };
            using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            c.DrawText(text, 240f, baseline, SKTextAlign.Center, font, outline);
            c.DrawText(text, 240f, baseline, SKTextAlign.Center, font, fill);
        }
        c.Restore();
    }

    private SKImage? _baseCache, _shellCache;
    private string _baseKey = "", _shellKey = "";

    /// <summary>Draws a layer once at the current size into a picture and keeps it until the size changes.</summary>
    private SKImage? Cached(ref SKImage? cache, ref string key, SKRect design, string[] assets, Action<SKCanvas> draw)
    {
        foreach (var asset in assets)
            if (RotomAssets.Get(asset) is null) return null;
        var wanted = $"{_scale:F4}|{design.Left:F1}|{design.Right:F1}";
        if (cache is null || key != wanted)
        {
            cache?.Dispose();
            cache = null;
            var width = (int)MathF.Ceiling(design.Width * _scale);
            var height = (int)MathF.Ceiling(design.Height * _scale);
            if (width <= 0 || height <= 0) return null;
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;
            var sc = surface.Canvas;
            sc.Clear(SKColors.Transparent);
            sc.Scale(width / design.Width, height / design.Height);
            sc.Translate(-design.Left, -design.Top);
            draw(sc);
            cache = surface.Snapshot();
            key = wanted;
        }
        return cache;
    }

    private static void DrawImage(SKCanvas c, string name, SKRect dest, float alpha = 1f)
    {
        var image = RotomAssets.Get(name);
        if (image is null) return;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(alpha * 255)) };
        c.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }

    private static SKRect At(float x, float y, SKImage? image, float scale = 0.5f) =>
        image is null ? SKRect.Empty : new SKRect(x, y, x + image.Width * scale, y + image.Height * scale);

    private void DrawRotom(SKCanvas c)
    {
        var eyes = RotomAssets.Get(_eyesClosed ? "rotomphone/rotom/rotom_eyes_closed.png" : "rotomphone/rotom/rotom_eyes_open.png");
        if (eyes is not null) c.DrawImage(eyes, At(262f, 94f, eyes), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

        if (_mouthOpen)
        {
            var open = RotomAssets.Get("rotomphone/rotom/rotom_mouth_open.png");
            if (open is not null) c.DrawImage(open, At(412f, 199f, open), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        else
        {
            var closed = RotomAssets.Get("rotomphone/rotom/rotom_mouth_closed.png");
            if (closed is not null) c.DrawImage(closed, At(414.5f, 239.5f, closed), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        // On the white screen the profile fades into the word MENU while the menu is open.
        var m = _menuProgress;
        if (m < 0.001f) DrawInfo(c);
        else
        {
            if (m < 0.999f)
            {
                using var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)((1f - m) * 255)) };
                c.SaveLayer(layerPaint);
                DrawInfo(c);
                c.Restore();
            }
            using var word = TextFont(57f);
            using var ink = Ink(InkGrey.WithAlpha((byte)(m * 255)));
            var metrics = word.Metrics;
            c.DrawText("MENU", 461f, 334.5f - (metrics.Ascent + metrics.Descent) / 2f, SKTextAlign.Center, word, ink);
        }
    }

    private void DrawInfo(SKCanvas c)
    {
        DrawAvatar(c);
        using (var name = TextFont(40f)) using (var ink = Ink(InkGrey))
        {
            var text = Fit(name, _userName, 150f, 40f, 24f, out var fitted);
            using var f = TextFont(fitted);
            c.DrawText(text, 328f, 321.5f + fitted * 0.35f, SKTextAlign.Left, f, ink);
        }
        using (var title = TextFont(24f)) using (var ink = Ink(InkGrey))
            c.DrawText("Dresseur", 371f, 370.5f + 24f * 0.35f, SKTextAlign.Left, title, ink);
        using (var stat = TextFont(24f)) using (var ink = Ink(InkGrey))
        {
            var line = _stats[Math.Min(_statIndex, _stats.Length - 1)];
            var text = Fit(stat, line, 216f, 24f, 16f, out var fitted);
            using var f = TextFont(fitted);
            c.DrawText(text, 485f, 325.5f + fitted * 0.35f, SKTextAlign.Left, f, ink);
        }
    }

    private static void DrawAvatar(SKCanvas c)
    {
        var picture = RotomAssets.Get("rotomphone/trainers/trainer_default.png");
        var circle = new SKRect(223f, 290f, 313f, 380f);
        c.Save();
        using var clip = new SKPath();
        clip.AddOval(circle);
        c.ClipPath(clip, SKClipOperation.Intersect, true);
        using (var back = new SKPaint { Color = SKColor.Parse("#DFF7F6"), IsAntialias = true }) c.DrawRect(circle, back);
        if (picture is not null)
        {
            // The head of the trainer sprite fills the circle.
            var side = Math.Min(picture.Width, picture.Height * 0.85f);
            var src = new SKRect((picture.Width - side) / 2f, 0f, (picture.Width + side) / 2f, side);
            c.DrawImage(picture, src, circle, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        c.Restore();
        using var ring = new SKPaint { Color = SKColor.Parse("#91EBE9"), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5f };
        c.DrawOval(new SKRect(circle.Left + 2.5f, circle.Top + 2.5f, circle.Right - 2.5f, circle.Bottom - 2.5f), ring);
    }

    private void DrawBubble(SKCanvas c)
    {
        if (_bubbleAlpha <= 0.01f || _bubble is null) return;
        var a = _bubbleAlpha;
        var shellRect = new SKRect(65f, 455f, 65f + 375f, 455f + 426f);
        var shell = Cached(ref _shellCache, ref _shellKey, shellRect, ["rotomphone/rotom/bubble_shell.png"],
            sc => DrawImage(sc, "rotomphone/rotom/bubble_shell.png", shellRect));
        if (shell is not null)
        {
            using var shellPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(a * 255)) };
            c.DrawImage(shell, shellRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), shellPaint);
        }

        var alpha = (byte)(a * 255);
        using var black = new SKPaint { Color = SKColors.Black.WithAlpha(alpha), IsAntialias = true };
        using (var f = TextFont(32f))
        {
            c.DrawText(_bubble.Number, 104f, 586f + 32f * 0.88f, SKTextAlign.Left, f, black);
            DrawBall(c, 104f + f.MeasureText(_bubble.Number) + 14f, 580f, a);
        }

        const float lineHeight = 39f;
        const float top = 644f;
        using var body = TextFont(24f);
        var name = Fit(body, _bubble.Name, 190f, 24f, 16f, out var nameSize);
        using (var nameFont = TextFont(nameSize))
        {
            c.DrawText(name, 82f, top + 24f * 0.88f, SKTextAlign.Left, nameFont, black);
            var x = 82f + nameFont.MeasureText(name) + 12f;
            foreach (var type in _bubble.Types)
            {
                if (type < 1 || type > RotomAssets.TypeNames.Length) continue;
                DrawImage(c, $"rotomphone/types/type_{RotomAssets.TypeNames[type - 1]}.png", new SKRect(x, 632f, x + 42f, 632f + 36f), a);
                x += 46f;
            }
        }
        for (var i = 0; i < _bubble.Lines.Count; i++)
        {
            var text = Fit(body, _bubble.Lines[i], 341f, 24f, 14f, out var size);
            using var f = TextFont(size);
            c.DrawText(text, 82f, top + (i + 1) * lineHeight + 24f * 0.88f, SKTextAlign.Left, f, black);
        }
    }

    /// <summary>The Poké Ball of the shown Pokémon, next to its number.</summary>
    private void DrawBall(SKCanvas c, float x, float y, float alpha)
    {
        if (_bubble is null || _bubble.Ball <= 0) return;
        var ball = _sprites.GetBall(_bubble.Ball);
        if (ball is null)
        {
            _sprites.WarmBall(_bubble.Ball, () => MainThread.BeginInvokeOnMainThread(InvalidateTop));
            return;
        }
        if (!ReferenceEquals(ball, _ballBitmap))
        {
            _ballImage?.Dispose();
            _ballImage = SKImage.FromBitmap(ball);
            _ballBitmap = ball;
        }
        if (_ballImage is null) return;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(alpha * 255)) };
        c.DrawImage(_ballImage, new SKRect(x, y, x + 44f, y + 44f), new SKSamplingOptions(SKFilterMode.Nearest), paint);
    }

    private void DrawPokemon(SKCanvas c)
    {
        if (_entry is null)
        {
            using var f = TextFont(40f);
            using var ink = Ink(SKColor.Parse("#5d8a89"));
            c.DrawText("Ta Banque est vide", 461f, 1150f, SKTextAlign.Center, f, ink);
            using var f2 = TextFont(26f);
            c.DrawText("Ajoute des Pokémon pour les voir ici", 461f, 1200f, SKTextAlign.Center, f2, ink);
            return;
        }

        var look = _entry.Info.Look;
        var bitmap = _sprites.GetHome(look);
        var pixelArt = false;
        if (bitmap is null)
        {
            if (!_sprites.HomeUnavailable(look)) _sprites.WarmHome(look, () => MainThread.BeginInvokeOnMainThread(InvalidateAll));
            else { bitmap = _sprites.GetSprite(look); pixelArt = true; }
        }
        if (bitmap is null) return;
        if (!ReferenceEquals(bitmap, _homeBitmap))
        {
            _homeImage?.Dispose();
            _homeImage = SKImage.FromBitmap(bitmap);
            _homeBitmap = bitmap;
        }
        var image = _homeImage;
        if (image is null) return;

        var fit = Math.Min(PokemonBox.Width / image.Width, PokemonBox.Height / image.Height);
        var w = image.Width * fit;
        var h = image.Height * fit;
        var dest = new SKRect(PokemonBox.MidX - w / 2f, PokemonBox.Bottom - h, PokemonBox.MidX + w / 2f, PokemonBox.Bottom);
        var sampling = pixelArt ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        // The shadow, outline and picture are composed once; repainting then costs one draw.
        if (_pokeCache is null || !ReferenceEquals(_pokeCacheSource, bitmap) || Math.Abs(_pokeCacheScale - _scale) > 0.001f)
        {
            _pokeCache?.Dispose();
            _pokeCache = null;
            var width = (int)MathF.Ceiling((PokemonBox.Width + 2 * PokemonPad) * _scale);
            var height = (int)MathF.Ceiling((PokemonBox.Height + 2 * PokemonPad) * _scale);
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is not null)
            {
                var sc = surface.Canvas;
                sc.Clear(SKColors.Transparent);
                sc.Scale(_scale);
                sc.Translate(PokemonPad - PokemonBox.Left, PokemonPad - PokemonBox.Top);
                ComposePokemon(sc, image, dest, sampling);
                _pokeCache = surface.Snapshot();
                _pokeCacheSource = bitmap;
                _pokeCacheScale = _scale;
            }
        }
        if (_pokeCache is null) return;
        c.DrawImage(_pokeCache,
            new SKRect(PokemonBox.Left - PokemonPad, PokemonBox.Top - PokemonPad, PokemonBox.Right + PokemonPad, PokemonBox.Bottom + PokemonPad),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }

    private static void ComposePokemon(SKCanvas c, SKImage image, SKRect dest, SKSamplingOptions sampling)
    {
        // Soft shadow behind.
        using (var shadow = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0, 0, 0, 90), SKBlendMode.SrcIn),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14f),
        })
            c.DrawImage(image, new SKRect(dest.Left, dest.Top + 22f, dest.Right, dest.Bottom + 22f), sampling, shadow);

        // White outline: the white silhouette repeated around the Pokémon.
        using (var white = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn) })
        {
            const float radius = 7f;
            for (var step = 0; step < 16; step++)
            {
                var angle = step * MathF.PI / 8f;
                var dx = MathF.Cos(angle) * radius;
                var dy = MathF.Sin(angle) * radius;
                c.DrawImage(image, new SKRect(dest.Left + dx, dest.Top + dy, dest.Right + dx, dest.Bottom + dy), sampling, white);
            }
        }
        c.DrawImage(image, dest, sampling);
    }

    private static SKFont TextFont(float size) => new(RotomFont.Face, size) { Edging = SKFontEdging.Antialias, Subpixel = true };

    private static SKPaint Ink(SKColor color) => new() { Color = color, IsAntialias = true };

    // Shrinks (down to a minimum size) then ellipsizes text to fit a width in design units; results are remembered.
    private static readonly Dictionary<(string, float, float, float), (string Text, float Size)> FitMemo = new();
    private static SKTypeface? _fitFace;

    private static string Fit(SKFont probe, string text, float width, float size, float min, out float fitted)
    {
        var face = RotomFont.Face;
        if (!ReferenceEquals(_fitFace, face)) { FitMemo.Clear(); _fitFace = face; }
        var key = (text, width, size, min);
        if (FitMemo.TryGetValue(key, out var known)) { fitted = known.Size; return known.Text; }
        if (FitMemo.Count > 200) FitMemo.Clear();
        var result = FitCompute(probe, text, width, size, min, out fitted);
        FitMemo[key] = (result, fitted);
        return result;
    }

    private static string FitCompute(SKFont probe, string text, float width, float size, float min, out float fitted)
    {
        fitted = size;
        using var font = new SKFont(probe.Typeface, size);
        while (fitted > min && font.MeasureText(text) > width)
        {
            fitted -= 1f;
            font.Size = fitted;
        }
        if (font.MeasureText(text) <= width) return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }

    // ── Touch ────────────────────────────────────────────────────────────────

    private SKPoint ToDesignTop(SKPoint p) => new((p.X - _offX) / _scale, p.Y / _scale);
    private SKPoint ToDesignBottom(SKPoint p) => new((p.X - _offX) / _scale, (p.Y - _botY) / _scale);

    private Target HitTest(SKPoint pixel)
    {
        var t = ToDesignTop(pixel);
        if (new SKRect(396f, 395f, 536f, 470f).Contains(t)) return Target.Handle;
        if (new SKRect(-130f, 180f, 125f, 530f).Contains(t)) return Target.LeftFlap;
        // Only the white info screen opens the profile.
        if (new SKRect(211f, 279f, 711f, 390f).Contains(t)) return Target.Profile;
        // The right half of the dark layer swaps in another Pokémon of the Bank (not the right flap, from about x 725 down).
        if (t.X >= DesignW / 2f && t.Y >= 0f && t.Y <= 390f && !(t.X >= 725f && t.Y >= 190f)) return Target.NextMon;
        if (_entry is not null && PokemonBox.Contains(ToDesignBottom(pixel))) return Target.Pokemon;
        return Target.None;
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                args.Handled = true;
                _pressItem = -1;
                _pressTarget = Target.None;
                if (_menuProgress > 0.001f)
                {
                    // While the menu is out only its buttons answer; nothing else on the screen does.
                    if (_menuOpen && _menuProgress >= 0.99f)
                    {
                        var d = ToDesignTop(args.Location);
                        for (var i = 0; i < _items.Length; i++)
                            if (_items[i].Action is not null && ItemRect(i).Contains(d))
                            {
                                _pressItem = i;
                                _pressTarget = Target.MenuItem;
                                _bottom.InvalidateSurface();
                                break;
                            }
                    }
                }
                else _pressTarget = HitTest(args.Location);
                _pressPoint = args.Location;
                _holdFired = false;
                _moved = false;
                _menuTriggered = false;
                if (_pressTarget == Target.Pokemon) StartHoldTimer();
                break;
            case SKTouchAction.Moved:
                args.Handled = true;
                var dx = args.Location.X - _pressPoint.X;
                var dy = args.Location.Y - _pressPoint.Y;
                var slop = 40f * Math.Max(1f, _scale);
                if (Math.Abs(dx) > slop || Math.Abs(dy) > slop)
                {
                    _moved = true;
                    _holdTimer?.Stop();
                    if (_pressItem >= 0) { _pressItem = -1; _bottom.InvalidateSurface(); }
                }
                // Swiping up anywhere closes the menu.
                if (_menuOpen && _menuProgress >= 0.99f && !_menuTriggered && dy < -80f * _scale)
                {
                    _menuTriggered = true;
                    _ = CloseMenuAsync();
                }
                if (_pressTarget == Target.Handle && !_menuTriggered && dy > 60f * _scale)
                {
                    _menuTriggered = true;
                    _ = OpenMenuAsync();
                }
                break;
            case SKTouchAction.Released:
                args.Handled = true;
                _holdTimer?.Stop();
                if (_pressItem >= 0)
                {
                    var item = _items[_pressItem];
                    _pressItem = -1;
                    _bottom.InvalidateSurface();
                    if (!_moved && item.Action is { } action)
                    {
                        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch (Exception) { /* no vibrator */ }
                        _ = RunMenuActionAsync(action);
                    }
                }
                else if (!_moved && !_holdFired) OnTap(_pressTarget);
                _pressTarget = Target.None;
                break;
            case SKTouchAction.Cancelled:
                _holdTimer?.Stop();
                if (_pressItem >= 0) { _pressItem = -1; _bottom.InvalidateSurface(); }
                _pressTarget = Target.None;
                break;
        }
    }

    private void StartHoldTimer()
    {
        _holdTimer ??= Dispatcher.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(HoldMs);
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick -= OnHold;
        _holdTimer.Tick += OnHold;
        _holdTimer.Start();
    }

    private void OnHold(object? sender, EventArgs e)
    {
        _holdTimer?.Stop();
        if (_pressTarget != Target.Pokemon || _moved) return;
        _holdFired = true;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); } catch (Exception) { /* no vibrator */ }
        _ = OpenSummaryAsync();
    }

    private void OnTap(Target target)
    {
        switch (target)
        {
            case Target.LeftFlap: ShowBubble(); break;
            case Target.Profile: _ = OpenAsync<RotomProfilePage>(); break;
            case Target.NextMon: ShowAnotherPokemon(); break;
            case Target.Pokemon:
                // The cry plays here once a cry source is chosen; for now a short tick tells the tap landed.
                try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch (Exception) { /* no vibrator */ }
                break;
        }
    }

    private void ShowAnotherPokemon()
    {
        var all = _bank.GetAll();
        if (all.Count < 2) return;
        BankEntry next;
        do { next = all[_random.Next(all.Count)]; } while (next.Id == _entry?.Id);
        _entry = next;
        _bubble = null;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch (Exception) { /* no vibrator */ }
        if (_bubbleUntil > _clock.ElapsedMilliseconds) ShowBubble();
        InvalidateAll();
    }

    // ── The yellow box ───────────────────────────────────────────────────────

    private void ShowBubble()
    {
        if (_entry is null) return;
        var now = _clock.ElapsedMilliseconds;
        _bubbleUntil = now + BubbleMs;
        _nextMouthToggle = now;
        if (_bubble is not null) { InvalidateTop(); return; }
        var entry = _entry;
        _ = Task.Run(() => BuildBubble(entry)).ContinueWith(t =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_entry?.Id == entry.Id && t.Status == TaskStatus.RanToCompletion) _bubble = t.Result;
                InvalidateTop();
            }));
    }

    private BubbleInfo BuildBubble(BankEntry entry)
    {
        var info = entry.Info;
        var strings = PKHeX.Core.GameInfo.GetStrings("fr");
        var species = info.Species > 0 && info.Species < strings.specieslist.Length ? strings.specieslist[info.Species] : "?";
        var lines = new List<string>();
        var ball = 0;
        if (!string.IsNullOrWhiteSpace(info.Nickname) && !string.Equals(info.Nickname, species, StringComparison.OrdinalIgnoreCase))
            lines.Add($"Surnom : {info.Nickname}");
        lines.Add($"Niveau : {info.Level}");
        try
        {
            var pk = EntityBytes.Parse(entry, _bank.GetData(entry.Id));
            if (pk is not null)
            {
                if (pk.MetDate is { } date) lines.Add($"Capturé le : {date.Day:00}/{date.Month:00}/{date.Year:0000}");
                lines.Add(RotomOrigin.Line(pk.Version));
                ball = pk.Ball;
            }
        }
        catch (Exception error)
        {
            Services.AppLog.Warn("rotom", $"Could not read origin of a Bank entry: {error.Message}");
        }
        return new BubbleInfo($"{info.Species:0000}", species, HabitatCatalog.TypesFor(info.Species, info.Form), lines, ball);
    }

    // ── Summary, profile, menu ───────────────────────────────────────────────

    private async Task OpenSummaryAsync()
    {
        if (_entry is null) return;
        var services = IPlatformApplication.Current!.Services;
        var engine = services.GetRequiredService<ISaveEngine>();
        var summaries = services.GetRequiredService<IMonSummaryService>();
        var entry = _entry;
        var deck = new SummaryDeck(1, _ => true, 0,
            (_, legality) => SummaryLoaders.FromBank(_bank, engine, summaries, entry, legality),
            "Banque");
        await MonSummaryScreen.ShowAsync(_root, _sprites, deck);
    }

    private async Task OpenAsync<TPage>() where TPage : Page
    {
        var services = IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("MAUI services are unavailable.");
        await Navigation.PushAsync(services.GetRequiredService<TPage>());
    }

    private async Task OpenOldHomeAsync(HomeStartAction action)
    {
        var services = IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("MAUI services are unavailable.");
        var page = services.GetRequiredService<HomePage>();
        page.StartAction = action;
        await Navigation.PushAsync(page);
    }

    private async Task RunMenuActionAsync(Func<Task> action)
    {
        if (_navigating) return;
        _navigating = true;
        try { await action(); }
        catch (Exception error) { Services.AppLog.Warn("rotom", $"Menu action failed: {error.Message}"); }
        finally { _navigating = false; }
    }

    private Task AnimateMenuAsync(float to, uint length, Easing easing)
    {
        this.AbortAnimation("rotomMenu");
        var done = new TaskCompletionSource();
        new Animation(v => { _menuProgress = (float)v; InvalidateAll(); }, _menuProgress, to, easing)
            .Commit(this, "rotomMenu", 16, length, finished: (_, _) => done.TrySetResult());
        return done.Task;
    }

    private async Task OpenMenuAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        _bubbleUntil = 0;
        _holdTimer?.Stop();
        // Build the picture before sliding so the animation itself stays smooth.
        EnsureMenuCache();
        await AnimateMenuAsync(1f, 260, Easing.CubicOut);
    }

    private async Task CloseMenuAsync()
    {
        if (!_menuOpen) return;
        _menuOpen = false;
        _pressItem = -1;
        await AnimateMenuAsync(0f, 220, Easing.CubicIn);
    }
}
