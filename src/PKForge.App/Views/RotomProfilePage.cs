using PKForge.App.Services;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Rotom Phone profile, built from the Figma frame "03 Profile" (1080 design units wide, scrolling).
/// From the top: the trainer card with the team of six, then one block per region with its games,
/// its Pokédex count and its badges. Everything is drawn on one canvas that scrolls by dragging.
/// </summary>
public sealed class RotomProfilePage : ContentPage
{
    private const float DesignW = 1080f;
    private const string TeamKey = "rotom_team_v1";
    private const int HoldMs = 600;

    private static readonly SKColor Teal = SKColor.Parse("#006e72");
    private static readonly SKColor Navy = SKColor.Parse("#243642");
    private static readonly SKColor Beige = SKColor.Parse("#e3d2c3");
    private static readonly SKColor Pill = SKColor.Parse("#eef7ff");
    private static readonly SKColor Ink = SKColor.Parse("#111820");
    private static readonly float[] TeamX = [52f, 221f, 390f, 559f, 728f, 897f];

    private enum BlockKind { National, Region, Go }

    private sealed record Block(BlockKind Kind, ProfileRegion? Region, float Top, float Height);

    private readonly IBankService _bank;
    private readonly ISpriteService _sprites;
    private readonly TrainerProfileStore _profiles;
    private readonly SKCanvasView _canvas;
    private readonly List<Block> _blocks = [];
    private readonly float _panelBottom;
    private readonly float _contentHeight;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private ProfileData _data = new("Dresseur", "Dresseur", 0, 0, new Dictionary<string, int>(), 0, [], []);
    private IReadOnlyList<BankEntry> _all = [];
    private readonly Guid?[] _team = new Guid?[6];

    // Scrolling
    private float _scale = 1f, _scrollY, _viewH;
    private bool _touching, _dragging;
    private SKPoint _pressPoint;
    private float _pressScroll, _lastY, _velocity;
    private long _lastMove;
    private int _pressSlot = -1;
    private bool _holdFired;
    private IDispatcherTimer? _flingTimer, _holdTimer;

    // Cached pictures: the wave background and the team Pokémon at the current size.
    private SKImage? _background;
    private string _backgroundKey = "";
    private readonly Dictionary<Guid, (SKBitmap Source, SKImage Image, int Size)> _teamImages = new();

    public RotomProfilePage(IBankService bank, ISpriteService sprites, TrainerProfileStore profiles)
    {
        _bank = bank;
        _sprites = sprites;
        _profiles = profiles;
        BackgroundColor = Color.FromArgb("#006e72");
        NavigationPage.SetHasNavigationBar(this, false);

        // The stack of blocks under the section bar, 15 units apart (as in the design).
        var y = 790f;
        void Add(BlockKind kind, ProfileRegion? region, float height)
        {
            _blocks.Add(new Block(kind, region, y, height));
            y += height + 15f;
        }
        Add(BlockKind.National, null, 301f);
        foreach (var region in ProfileCatalog.Regions) Add(BlockKind.Region, region, region.CardHeight);
        Add(BlockKind.Go, null, 337f);
        _panelBottom = y - 15f + 30f;
        _contentHeight = _panelBottom + 26f;

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _canvas.PaintSurface += OnPaint;
        _canvas.Touch += OnTouch;
        Content = _canvas;

        _ = RotomAssets.WarmAsync(ProfileCatalog.AssetNames()).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
        _ = RotomFont.WarmAsync().ContinueWith(_ => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
    }

    // ── Lifecycle and data ───────────────────────────────────────────────────

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadTeam();
        Refresh();
    }

    protected override void OnDisappearing()
    {
        _flingTimer?.Stop();
        _holdTimer?.Stop();
        base.OnDisappearing();
    }

    /// <summary>Numbers that need no reading are shown at once; the origin-based counts follow from a background pass.</summary>
    private void Refresh()
    {
        _all = _bank.GetAll();
        var name = _profiles.Profiles.FirstOrDefault()?.DisplayName is { Length: > 0 } n ? n : "Dresseur";
        _data = ProfileStats.Quick(_all, name);
        _canvas.InvalidateSurface();

        var all = _all;
        _ = Task.Run(() => ProfileStats.Deep(_bank, all, name)).ContinueWith(t =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                _data = t.Result;
                _canvas.InvalidateSurface();
            }));
    }

    private void LoadTeam()
    {
        Array.Clear(_team);
        try
        {
            var parts = Preferences.Default.Get(TeamKey, "").Split(',');
            for (var i = 0; i < _team.Length && i < parts.Length; i++)
                if (Guid.TryParse(parts[i], out var id)) _team[i] = id;
        }
        catch (Exception) { /* no saved team */ }
    }

    private void SaveTeam()
    {
        try { Preferences.Default.Set(TeamKey, string.Join(',', _team.Select(id => id?.ToString("N") ?? "-"))); }
        catch (Exception) { /* the team just is not remembered */ }
    }

    private BankEntry? TeamEntry(int slot) =>
        _team[slot] is { } id ? _all.FirstOrDefault(e => e.Id == id) : null;

    private async Task PickForSlotAsync(int slot)
    {
        var services = IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("MAUI services are unavailable.");
        var bankPage = services.GetRequiredService<BankPage>();
        bankPage.PickHandler = entry =>
        {
            // A Pokémon sits in one slot only: choosing it again moves it here.
            for (var i = 0; i < _team.Length; i++)
                if (_team[i] == entry.Id) _team[i] = null;
            _team[slot] = entry.Id;
            SaveTeam();
        };
        await Navigation.PushAsync(bankPage);
    }

    // ── Painting ─────────────────────────────────────────────────────────────

    private static SKFont Font(float size, bool bold = true) =>
        new(RotomFont.Face, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Embolden = bold };

    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        float w = e.Info.Width, h = e.Info.Height;
        if (w <= 0 || h <= 0) return;
        _scale = w / DesignW;
        _viewH = h;
        _scrollY = Math.Clamp(_scrollY, 0f, MaxScroll());

        c.Clear(Teal);
        DrawBackground(c, e.Info);

        c.Save();
        c.Translate(0, -_scrollY);
        c.Scale(_scale);
        var top = _scrollY / _scale;
        var bottom = (_scrollY + h) / _scale;

        DrawHeader(c);
        DrawPanel(c, top, bottom);
        c.Restore();
    }

    private float MaxScroll() => Math.Max(0f, _contentHeight * _scale - _viewH);

    /// <summary>The wave pattern behind everything, tiled once at the current size.</summary>
    private void DrawBackground(SKCanvas c, SKImageInfo info)
    {
        var tile = RotomAssets.Get("rotomphone/profile/wave_tile.png");
        if (tile is null) return;
        var key = $"{info.Width}x{info.Height}";
        if (_background is null || _backgroundKey != key)
        {
            _background?.Dispose();
            _background = null;
            using var surface = SKSurface.Create(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return;
            var sc = surface.Canvas;
            sc.Clear(SKColors.Transparent);
            var tw = tile.Width * _scale;
            var th = tile.Height * _scale;
            for (var ty = 0f; ty < info.Height; ty += th)
                for (var tx = 0f; tx < info.Width; tx += tw)
                    sc.DrawImage(tile, new SKRect(MathF.Floor(tx), MathF.Floor(ty), MathF.Ceiling(tx + tw), MathF.Ceiling(ty + th)), Smooth);
            _background = surface.Snapshot();
            _backgroundKey = key;
        }
        c.DrawImage(_background, 0, 0);
    }

    private static void Fill(SKCanvas c, SKRect r, float radius, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRoundRect(r, radius, radius, paint);
    }

    private static void DrawImageFit(SKCanvas c, string name, SKRect box, float alpha = 1f, bool bottomAlign = false)
    {
        var image = RotomAssets.Get(name);
        if (image is null) return;
        var fit = Math.Min(box.Width / image.Width, box.Height / image.Height);
        var w = image.Width * fit;
        var h = image.Height * fit;
        var left = box.MidX - w / 2f;
        var topY = bottomAlign ? box.Bottom - h : box.MidY - h / 2f;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(alpha * 255)) };
        c.DrawImage(image, new SKRect(left, topY, left + w, topY + h), Smooth, paint);
    }

    /// <summary>Text centred vertically on <paramref name="centreY"/>.</summary>
    private static void DrawText(SKCanvas c, string text, float x, float centreY, SKFont font, SKColor color, SKTextAlign align)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var m = font.Metrics;
        c.DrawText(text, x, centreY - (m.Ascent + m.Descent) / 2f, align, font, paint);
    }

    /// <summary>The largest size (down to a minimum) at which the text fits the width.</summary>
    private static float FitSize(string text, float width, float size, float min)
    {
        using var font = Font(size);
        while (size > min && font.MeasureText(text) > width)
        {
            size -= 1f;
            font.Size = size;
        }
        return size;
    }

    private void DrawHeader(SKCanvas c)
    {
        // The card: a beige border around the dark card.
        Fill(c, new SKRect(40, 41, 1039, 634), 44f, Beige);
        Fill(c, new SKRect(52, 53, 1027, 622), 34f, Navy);

        // Trainer picture on its teal square.
        var square = new SKRect(63, 64, 293, 294);
        using (var back = new SKPaint { Color = SKColor.Parse("#07656a") }) c.DrawRect(square, back);
        var picture = RotomAssets.Get("rotomphone/trainers/trainer_default.png");
        if (picture is not null)
        {
            var side = Math.Min(picture.Width, picture.Height * 0.85f);
            var src = new SKRect((picture.Width - side) / 2f, 0f, (picture.Width + side) / 2f, side);
            c.DrawImage(picture, src, square, Smooth);
        }

        // Name, title and Pokédex count: two columns of cells.
        var label = Pill;
        var cells = new (float Y, SKColor Fill, string Left, string Right)[]
        {
            (91f, SKColor.Parse("#31535d"), "Nom :", _data.Name),
            (142f, SKColor.Parse("#476568"), "Titre :", _data.Title),
            (217f, SKColor.Parse("#63706e"), "Pokédex National :", $"{_data.NationalCaught}/{ProfileCatalog.NationalTotal}"),
        };
        foreach (var (y, fill, left, right) in cells)
        {
            using var paint = new SKPaint { Color = fill };
            c.DrawRect(new SKRect(311, y, 644, y + 51), paint);
            c.DrawRect(new SKRect(672, y, 1005, y + 51), paint);
            using (var f = Font(FitSize(left, 310, 30, 20))) DrawText(c, left, 323f, y + 25.5f, f, label, SKTextAlign.Left);
            using (var f = Font(FitSize(right, 300, 30, 18))) DrawText(c, right, 684f, y + 25.5f, f, label, SKTextAlign.Left);
        }

        // The "Statistiques" caption with its two lines.
        using (var line = new SKPaint { Color = Pill, IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke })
        {
            c.DrawLine(69, 354, 242, 354, line);
            c.DrawLine(839, 354, 1012, 354, line);
        }
        Fill(c, new SKRect(260, 314, 820, 394), 40f, Pill);
        using (var f = Font(44f)) DrawText(c, "Statistiques", 540f, 354f, f, Ink, SKTextAlign.Center);

        // The team: six slots; an empty one shows a plus.
        for (var i = 0; i < 6; i++) DrawTeamSlot(c, i);
    }

    private void DrawTeamSlot(SKCanvas c, int slot)
    {
        var rect = new SKRect(TeamX[slot], 484, TeamX[slot] + 130, 614);
        var entry = TeamEntry(slot);
        var image = entry is null ? null : TeamImage(entry);
        if (image is not null)
        {
            c.DrawImage(image, rect, Smooth);
            return;
        }
        if (entry is not null) return; // the picture is still loading
        Fill(c, rect, 32f, SKColor.Parse("#31535d"));
        using var plus = new SKPaint
        {
            Color = SKColor.Parse("#7f9aa0"), IsAntialias = true, StrokeWidth = 9f, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke,
        };
        c.DrawLine(rect.MidX - 24, rect.MidY, rect.MidX + 24, rect.MidY, plus);
        c.DrawLine(rect.MidX, rect.MidY - 24, rect.MidX, rect.MidY + 24, plus);
    }

    /// <summary>The Pokémon's picture scaled once to its slot (a big picture shrunk every frame would make scrolling stutter).</summary>
    private SKImage? TeamImage(BankEntry entry)
    {
        var look = entry.Info.Look;
        var bitmap = _sprites.GetHome(look);
        if (bitmap is null)
        {
            if (!_sprites.HomeUnavailable(look)) _sprites.WarmHome(look, () => MainThread.BeginInvokeOnMainThread(_canvas.InvalidateSurface));
            else bitmap = _sprites.GetSprite(look);
        }
        if (bitmap is null) return null;
        var size = Math.Max(1, (int)MathF.Ceiling(130f * _scale));
        if (_teamImages.TryGetValue(entry.Id, out var known) && ReferenceEquals(known.Source, bitmap) && known.Size == size) return known.Image;

        using var source = SKImage.FromBitmap(bitmap);
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (source is null || surface is null) return null;
        var fit = Math.Min(size / (float)source.Width, size / (float)source.Height);
        var w = source.Width * fit;
        var h = source.Height * fit;
        surface.Canvas.Clear(SKColors.Transparent);
        // Home art is smooth; the small pixel sprite stays crisp.
        var sampling = bitmap.Width <= 128 ? new SKSamplingOptions(SKFilterMode.Nearest) : Smooth;
        surface.Canvas.DrawImage(source, new SKRect((size - w) / 2f, size - h, (size + w) / 2f, size), sampling);
        var image = surface.Snapshot();
        if (_teamImages.TryGetValue(entry.Id, out var old)) old.Image.Dispose();
        _teamImages[entry.Id] = (bitmap, image, size);
        return image;
    }

    private void DrawPanel(SKCanvas c, float top, float bottom)
    {
        // The big beige panel with its dark title bar.
        Fill(c, new SKRect(40, 674, 1039, _panelBottom), 44f, Beige);
        c.Save();
        c.ClipRect(new SKRect(40, 674, 1039, 790));
        Fill(c, new SKRect(40, 674, 1039, 840), 44f, Navy);
        c.Restore();
        var title = "Avancement des Pokédex";
        using (var f = Font(FitSize(title, 800, 56, 30))) DrawText(c, title, 540f, 732f, f, Pill, SKTextAlign.Center);

        foreach (var block in _blocks)
        {
            if (block.Top + block.Height < top || block.Top > bottom) continue;
            switch (block.Kind)
            {
                case BlockKind.National: DrawNational(c, block); break;
                case BlockKind.Region: DrawRegion(c, block); break;
                case BlockKind.Go: DrawGo(c, block); break;
            }
        }
    }

    private static void DrawBar(SKCanvas c, Block block, string title, string count)
    {
        var bar = new SKRect(121f, block.Top + 184f, 961f, block.Top + 184f + 99f);
        Fill(c, bar, 49.5f, Pill);
        using (var f = Font(FitSize(count, 220, 52, 30))) DrawText(c, count, bar.Right - 28f, bar.MidY, f, Ink, SKTextAlign.Right);
        using (var f = Font(FitSize(title, 560, 52, 26))) DrawText(c, title, bar.Left + 26f, bar.MidY, f, Ink, SKTextAlign.Left);
    }

    private void DrawNational(SKCanvas c, Block block)
    {
        Fill(c, new SKRect(66, block.Top, 1014, block.Top + block.Height), 40f, Navy);
        DrawImageFit(c, "rotomphone/profile/icon_national.png", new SKRect(476, block.Top + 20, 604, block.Top + 148));
        DrawBar(c, block, "Pokémon capturés", _data.Captured.ToString());
    }

    private void DrawGo(SKCanvas c, Block block)
    {
        Fill(c, new SKRect(66, block.Top, 1014, block.Top + block.Height), 40f, Navy);
        DrawBar(c, block, "Pokédex de GO", _data.GoCaught.ToString());
    }

    private void DrawRegion(SKCanvas c, Block block)
    {
        var region = block.Region!;
        Fill(c, new SKRect(66, block.Top, 1014, block.Top + block.Height), 40f, Navy);

        // Games: dimmed until a save of that game has been imported.
        var count = region.Games.Length;
        var gap = count > 1 ? Math.Min(32f, (934f - 128f * count) / (count - 1)) : 0f;
        var x = 540f - (128f * count + gap * (count - 1)) / 2f;
        foreach (var game in region.Games)
        {
            var owned = _data.OwnedGames.Contains($"{region.Id}/{game.Id}");
            DrawImageFit(c, ProfileCatalog.GameAsset(region.Id, game.Id), new SKRect(x, block.Top + 18, x + 128, block.Top + 146), owned ? 1f : 0.4f);
            x += 128f + gap;
        }

        var caught = _data.RegionCaught.TryGetValue(region.Id, out var n) ? n : 0;
        DrawBar(c, block, region.Title, $"{caught}/{ProfileCatalog.Total(region.Id)}");

        var pill = new SKRect(122f, block.Top + 319f, 962f, block.Top + 319f + region.BadgePillHeight);
        Fill(c, pill, Math.Min(region.BadgePillHeight / 2f, 100f), Pill);
        DrawBadges(c, region, pill);
    }

    private float BadgeAlpha(ProfileRegion region, string badge) =>
        _data.OwnedBadges.Contains($"{region.Id}/{badge}") ? 1f : 0.4f;

    private void DrawBadges(SKCanvas c, ProfileRegion region, SKRect pill)
    {
        var names = region.Badges;
        switch (region.Style)
        {
            case BadgeStyle.Row:
            case BadgeStyle.Large:
                DrawBadgeRow(c, region, names, pill, region.Style == BadgeStyle.Large ? 80f : 64f, region.Style == BadgeStyle.Large ? 640f : 757f, pill.MidY);
                break;
            case BadgeStyle.Zigzag:
            {
                // Eighteen diamonds, each one a little lower or higher than its neighbour.
                var pitch = (792f - 80f) / Math.Max(1, names.Length - 1);
                var left = pill.MidX - 792f / 2f;
                for (var i = 0; i < names.Length; i++)
                {
                    var boxTop = pill.Top - 1f + (i % 2 == 0 ? 0f : 22f);
                    DrawImageFit(c, ProfileCatalog.BadgeAsset(region.Id, names[i]),
                        new SKRect(left + i * pitch, boxTop, left + i * pitch + 80f, boxTop + 80f), BadgeAlpha(region, names[i]));
                }
                break;
            }
            case BadgeStyle.Pair:
            {
                var left = pill.MidX - (2 * 165f + 20f) / 2f;
                for (var i = 0; i < names.Length; i++)
                    DrawImageFit(c, ProfileCatalog.BadgeAsset(region.Id, names[i]),
                        new SKRect(left + i * 185f, pill.MidY - 82.5f, left + i * 185f + 165f, pill.MidY + 82.5f), BadgeAlpha(region, names[i]));
                break;
            }
            case BadgeStyle.Rows:
            {
                // 8 + 5 + 5 round medals.
                int[] perRow = [8, 5, 5];
                var index = 0;
                var rowTop = pill.Top + 27f;
                foreach (var count in perRow)
                {
                    var left = pill.MidX - (96f * (count - 1) + 66f) / 2f;
                    for (var i = 0; i < count && index < names.Length; i++, index++)
                        DrawImageFit(c, ProfileCatalog.BadgeAsset(region.Id, names[index]),
                            new SKRect(left + i * 96f, rowTop, left + i * 96f + 66f, rowTop + 66f), BadgeAlpha(region, names[index]));
                    rowTop += 81f;
                }
                break;
            }
        }
    }

    /// <summary>One line of badges, evenly spaced across the pill.</summary>
    private void DrawBadgeRow(SKCanvas c, ProfileRegion region, string[] names, SKRect pill, float height, float width, float centreY)
    {
        var images = names.Select(n => RotomAssets.Get(ProfileCatalog.BadgeAsset(region.Id, n))).ToArray();
        var widths = images.Select(i => i is null ? height : i.Width * (height / i.Height)).ToArray();
        // Keep a very wide badge from crowding its neighbours.
        for (var i = 0; i < widths.Length; i++) widths[i] = Math.Min(widths[i], height * 1.4f);
        var free = width - widths.Sum();
        var spacing = names.Length > 1 ? Math.Clamp(free / (names.Length - 1), 6f, 48f) : 0f;
        var x = pill.MidX - (widths.Sum() + spacing * (names.Length - 1)) / 2f;
        for (var i = 0; i < names.Length; i++)
        {
            DrawImageFit(c, ProfileCatalog.BadgeAsset(region.Id, names[i]),
                new SKRect(x, centreY - height / 2f, x + widths[i], centreY + height / 2f), BadgeAlpha(region, names[i]));
            x += widths[i] + spacing;
        }
    }

    // ── Touch: drag to scroll, tap a team slot to choose, hold to empty it ──

    private int SlotAt(SKPoint p)
    {
        var x = p.X / _scale;
        var y = (p.Y + _scrollY) / _scale;
        for (var i = 0; i < 6; i++)
            if (x >= TeamX[i] && x <= TeamX[i] + 130 && y >= 484 && y <= 614) return i;
        return -1;
    }

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                args.Handled = true;
                _flingTimer?.Stop();
                _touching = true;
                _dragging = false;
                _holdFired = false;
                _pressPoint = args.Location;
                _pressScroll = _scrollY;
                _lastY = args.Location.Y;
                _velocity = 0f;
                _lastMove = _clock.ElapsedMilliseconds;
                _pressSlot = SlotAt(args.Location);
                if (_pressSlot >= 0 && TeamEntry(_pressSlot) is not null) StartHoldTimer();
                break;
            case SKTouchAction.Moved:
                args.Handled = true;
                if (!_touching) break;
                var dy = args.Location.Y - _pressPoint.Y;
                if (!_dragging && (Math.Abs(dy) > 14f * Math.Max(1f, _scale) || Math.Abs(args.Location.X - _pressPoint.X) > 40f))
                {
                    _dragging = true;
                    _holdTimer?.Stop();
                }
                if (_dragging)
                {
                    var now = _clock.ElapsedMilliseconds;
                    var elapsed = Math.Max(1, now - _lastMove);
                    _velocity = (_lastY - args.Location.Y) / elapsed * 1000f;
                    _lastY = args.Location.Y;
                    _lastMove = now;
                    _scrollY = Math.Clamp(_pressScroll - dy, 0f, MaxScroll());
                    _canvas.InvalidateSurface();
                }
                break;
            case SKTouchAction.Released:
                args.Handled = true;
                _holdTimer?.Stop();
                _touching = false;
                if (_dragging)
                {
                    if (_clock.ElapsedMilliseconds - _lastMove > 90) _velocity = 0f;
                    StartFling();
                }
                else if (!_holdFired && _pressSlot >= 0) _ = PickForSlotAsync(_pressSlot);
                break;
            case SKTouchAction.Cancelled:
                _holdTimer?.Stop();
                _touching = false;
                break;
        }
    }

    private void StartFling()
    {
        if (Math.Abs(_velocity) < 80f) return;
        _flingTimer ??= Dispatcher.CreateTimer();
        _flingTimer.Interval = TimeSpan.FromMilliseconds(16);
        _flingTimer.Tick -= OnFling;
        _flingTimer.Tick += OnFling;
        _flingTimer.Start();
    }

    private void OnFling(object? sender, EventArgs e)
    {
        _scrollY = Math.Clamp(_scrollY + _velocity * 0.016f, 0f, MaxScroll());
        _velocity *= 0.94f;
        _canvas.InvalidateSurface();
        if (Math.Abs(_velocity) < 40f || _scrollY <= 0f || _scrollY >= MaxScroll()) _flingTimer?.Stop();
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
        if (_dragging || _pressSlot < 0 || _team[_pressSlot] is null) return;
        _holdFired = true;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); } catch (Exception) { /* no vibrator */ }
        _team[_pressSlot] = null;
        SaveTeam();
        _canvas.InvalidateSurface();
    }
}
