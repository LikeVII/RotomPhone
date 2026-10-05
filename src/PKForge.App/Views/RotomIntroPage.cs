using Plugin.Maui.Rive;

namespace PKForge.App.Views;

/// <summary>
/// The opening screen (Figma frame "INTRODUCTION", 922 x 2048 design units): Rotom's face with
/// animated eyes and the "Touchez l'écran pour commencer" band. Any touch opens the home screen.
/// If anything about the opening screen cannot load, the app goes straight to the home screen.
/// </summary>
public sealed class RotomIntroPage : ContentPage
{
    private const float DesignW = 922f, DesignH = 2048f;
    private const string BackgroundAsset = "rotomphone/intro/intro_background.png";

    // intro_eyes.riv is a 500 x 500 artboard that holds the design's "Eyes" frame (864.24 units wide at
    // (29, 557)) scaled to 333 units wide and placed 83.5 / 159.5 units from the artboard's top-left.
    private const float EyesFrameX = 29f, EyesFrameY = 557f;
    private const float ArtboardScale = 333f / 864.2368f;
    private const float ArtboardInsetX = 83.5f, ArtboardInsetY = 159.5f, ArtboardSize = 500f;

    private readonly Grid _root = new() { BackgroundColor = Color.FromRgb(238, 43, 37) };
    private readonly Image _background;
    private readonly Random _random = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private RiveAnimationView? _eyes;
    private IDispatcherTimer? _timer;
    private long _nextPlay;
    private bool _leaving;

    public RotomIntroPage()
    {
        BackgroundColor = Color.FromRgb(238, 43, 37);
        NavigationPage.SetHasNavigationBar(this, false);

        _background = new Image
        {
            Aspect = Aspect.Fill,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true,
            Source = ImageSource.FromStream(() => FileSystem.OpenAppPackageFileAsync(BackgroundAsset).GetAwaiter().GetResult()),
        };
        _root.Add(_background);

        // Receives the touch, whatever is underneath.
        var touch = new BoxView { Color = Colors.Transparent };
        touch.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _ = GoHomeAsync()) });
        _root.Add(touch);
        _root.SizeChanged += (_, _) => Position();
        Content = _root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            // The picture must be readable, otherwise there is nothing to show.
            await using var probe = await FileSystem.OpenAppPackageFileAsync(BackgroundAsset);
        }
        catch (Exception error)
        {
            Services.AppLog.Warn("rotom", $"Opening screen unavailable: {error.Message}");
            await GoHomeAsync();
            return;
        }
        AttachEyes();
        _nextPlay = _clock.ElapsedMilliseconds + 2500;
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        DetachEyes();
        base.OnDisappearing();
    }

    private void AttachEyes()
    {
        if (_eyes is not null) return;
        // Preview-quality plugin: without the eyes the screen still works.
        try
        {
            var eyes = new RiveAnimationView
            {
                ResourceName = "intro_eyes",
                AutoPlay = true,
                Fit = RiveFitMode.Contain,
                RiveAlignment = RiveAlignmentMode.Center,
                InputTransparent = true,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
            };
            _root.Insert(1, eyes);
            _eyes = eyes;
            Position();
        }
        catch (Exception error)
        {
            Services.AppLog.Warn("rotom", $"Opening eyes unavailable: {error.Message}");
        }
    }

    private void DetachEyes()
    {
        var eyes = _eyes;
        _eyes = null;
        if (eyes is null) return;
        try { _root.Remove(eyes); }
        catch (Exception error) { Services.AppLog.Warn("rotom", $"Opening eyes removal failed: {error.Message}"); }
    }

    /// <summary>The picture is as wide as the screen and centred; the eyes follow the same scale.</summary>
    private void Position()
    {
        if (_root.Width <= 0 || _root.Height <= 0) return;
        var unit = (float)_root.Width / DesignW;
        var imageHeight = DesignH * unit;
        var top = ((float)_root.Height - imageHeight) / 2f;
        _background.WidthRequest = _root.Width;
        _background.HeightRequest = imageHeight;

        var eyes = _eyes;
        if (eyes is null) return;
        var size = ArtboardSize / ArtboardScale * unit;
        eyes.WidthRequest = size;
        eyes.HeightRequest = size;
        eyes.TranslationX = (EyesFrameX - ArtboardInsetX / ArtboardScale) * unit;
        eyes.TranslationY = top + (EyesFrameY - ArtboardInsetY / ArtboardScale) * unit;
    }

    /// <summary>The animation plays again at uneven moments, sometimes twice in a row, so it never looks mechanical.</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.ElapsedMilliseconds;
        if (_eyes is null || now < _nextPlay) return;
        var quick = _random.NextDouble() < 0.2;
        _nextPlay = now + (quick ? 500 + _random.Next(400) : 1800 + _random.Next(4800));
        try { _eyes.Play("Timeline 1", RiveLoopMode.OneShot); }
        catch (Exception error) { Services.AppLog.Warn("rotom", $"Opening eyes replay failed: {error.Message}"); }
    }

    private async Task GoHomeAsync()
    {
        if (_leaving) return;
        _leaving = true;
        _timer?.Stop();
        await Task.Yield();
        var services = IPlatformApplication.Current?.Services;
        if (services is null || Window is null) return;
        // The eyes stay on screen until the home screen replaces this page (OnDisappearing removes them);
        // taking them away first left a face without eyes while the home screen was being prepared.
        var home = services.GetRequiredService<RotomHomePage>();
        Window.Page = new NavigationPage(home)
        {
            BarBackgroundColor = Theme.UiTokens.Navy1,
            BarTextColor = Colors.White,
        };
    }
}
