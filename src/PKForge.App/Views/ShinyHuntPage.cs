using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The shiny hunting part: counters, the shiny collection and the ShinyDex. A temporary layout copied from
/// a reference site until the final design exists.
/// </summary>
public sealed class ShinyHuntPage : ContentPage
{
    private readonly ShinyCountersView _counters;
    private readonly ShinyCollectionView _collection;
    private readonly ShinyDexView _dex;
    private readonly ContentView _body = new();
    private readonly Grid _overlay = new() { IsVisible = false, BackgroundColor = Color.FromArgb("#C0000000") };
    private readonly Button[] _tabs = new Button[3];
    private readonly BoxView[] _tabLines = new BoxView[3];
    private IDispatcherTimer? _timer;
    private int _tab;

    public ShinyHuntPage(ShinyStore store, ISpriteService sprites, IGameDataService data)
    {
        Store = store;
        Sprites = sprites;
        Data = data;
        BackgroundColor = ShinyUi.Background;
        NavigationPage.SetHasNavigationBar(this, false);

        _counters = new ShinyCountersView(this);
        _collection = new ShinyCollectionView(this);
        _dex = new ShinyDexView(this);

        var back = new Button
        {
            Text = "‹",
            FontSize = 30,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 56,
            HeightRequest = 48,
            Padding = 0,
        };
        back.Clicked += async (_, _) => await Navigation.PopAsync();
        var header = new Grid
        {
            BackgroundColor = ShinyUi.Bar,
            Padding = new Thickness(8, 34, 8, 8),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(56)) },
        };
        header.Add(back);
        var title = ShinyUi.Txt("Chasse aux chromatiques", 22, bold: true, align: TextAlignment.Center);
        Grid.SetColumn(title, 1);
        header.Add(title);

        var bar = new Grid { BackgroundColor = ShinyUi.Bar, Padding = new Thickness(0, 0, 0, 6) };
        string[] names = ["Compteurs", "Collection", "ShinyDex"];
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var stack = new VerticalStackLayout { Spacing = 0 };
            var button = new Button
            {
                Text = names[i],
                FontFamily = ShinyUi.FontName,
                FontSize = 17,
                BackgroundColor = Colors.Transparent,
                HeightRequest = 52,
                Padding = 0,
            };
            button.Clicked += (_, _) => SelectTab(index);
            var line = new BoxView { HeightRequest = 4, Color = Colors.Transparent, CornerRadius = 2, Margin = new Thickness(20, 0) };
            _tabs[i] = button;
            _tabLines[i] = line;
            stack.Add(button);
            stack.Add(line);
            Grid.SetColumn(stack, i);
            bar.Add(stack);
        }

        var root = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
        };
        root.Add(header);
        Grid.SetRow(_body, 1);
        root.Add(_body);
        Grid.SetRow(bar, 2);
        root.Add(bar);
        Grid.SetRowSpan(_overlay, 3);
        root.Add(_overlay);
        Content = root;
        SelectTab(0);
    }

    public ShinyStore Store { get; }

    public ISpriteService Sprites { get; }

    public IGameDataService Data { get; }

    private void SelectTab(int index)
    {
        _tab = index;
        for (var i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].TextColor = i == index ? ShinyUi.AccentText : ShinyUi.Muted;
            _tabLines[i].Color = i == index ? ShinyUi.Accent : Colors.Transparent;
        }
        switch (index)
        {
            case 0: _counters.Rebuild(); _body.Content = _counters; break;
            case 1: _collection.Rebuild(); _body.Content = _collection; break;
            default: _dex.Rebuild(); _body.Content = _dex; break;
        }
    }

    /// <summary>Counters and collection changed elsewhere (a hunt was caught): show them fresh.</summary>
    public void RefreshAll()
    {
        switch (_tab)
        {
            case 0: _counters.Rebuild(); break;
            case 1: _collection.Rebuild(); break;
            default: _dex.Rebuild(); break;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshAll();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_tab == 0) _counters.Tick();
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_overlay.IsVisible) return base.OnBackButtonPressed();
        HideOverlay();
        return true;
    }

    // ── Shared helpers for the three views ───────────────────────────────────

    public void ShowOverlay(View view)
    {
        _overlay.Children.Clear();
        _overlay.Add(view);
        _overlay.IsVisible = true;
    }

    public void HideOverlay()
    {
        _overlay.IsVisible = false;
        _overlay.Children.Clear();
    }

    /// <summary>The pop-up to start a hunt, or to change the settings of one.</summary>
    public void ShowCreate(Hunt? editing)
    {
        ShowOverlay(new ShinyCreateDialog(this, editing, () =>
        {
            HideOverlay();
            SelectTab(0);
        }));
    }

    public Task<string?> AskAsync(string title, string message, string initial, bool numeric) =>
        DisplayPromptAsync(title, message, "OK", "Annuler", null, -1, numeric ? Keyboard.Numeric : Keyboard.Text, initial);

    public Task<bool> ConfirmAsync(string title, string message, string accept) =>
        DisplayAlertAsync(title, message, accept, "Annuler");

    public Task NoticeAsync(string title, string message) => DisplayAlertAsync(title, message, "OK");

    public Task<string?> ChooseAsync(string title, params string[] options) =>
        DisplayActionSheetAsync(title, "Annuler", null, options);
}
