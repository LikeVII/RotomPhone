using PKForge.App.Services;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.App.Views;

/// <summary>One line of the Pokémon list.</summary>
internal sealed record SpeciesRow(int Id, string Name);

/// <summary>Picks a Poké Ball from the game's ball names.</summary>
internal static class BallPicker
{
    public static async Task<int?> ChooseAsync(ShinyHuntPage page, int current)
    {
        var names = new List<string>();
        var ids = new List<int>();
        for (var id = 1; id < page.Data.BallNames.Count; id++)
        {
            var name = page.Data.BallNames[id];
            if (name.Length == 0 || names.Contains(name)) continue;
            names.Add(name);
            ids.Add(id);
        }
        var shown = current > 0 ? $"Poké Ball (actuelle : {ShinyUi.BallName(page.Data, current)})" : "Poké Ball";
        var choice = await page.ChooseAsync(shown, [.. names]);
        var index = choice is null ? -1 : names.IndexOf(choice);
        return index >= 0 ? ids[index] : null;
    }
}

/// <summary>The bundled picture of a game, found through the profile screen's game icons.</summary>
internal static class GameIcons
{
    public static ImageSource? For(GameVersion version)
    {
        foreach (var region in ProfileCatalog.Regions)
        {
            foreach (var game in region.Games)
            {
                if (!game.Versions.Contains(version)) continue;
                var asset = ProfileCatalog.GameAsset(region.Id, game.Id);
                return ImageSource.FromStream(async _ => (Stream?)await FileSystem.OpenAppPackageFileAsync(asset));
            }
        }
        return null;
    }
}

/// <summary>A row of a list: a Pokémon with its number and picture.</summary>
internal sealed class SpeciesCell : Border
{
    private readonly ShinyHuntPage _page;
    private readonly Func<int> _selected;
    private readonly Label _name = ShinyUi.Txt("", 22, ShinyUi.AccentText, bold: true);
    private readonly Label _number = ShinyUi.Txt("", 16, ShinyUi.AccentText);
    private readonly SpriteImage _image = new() { HeightRequest = 66, WidthRequest = 70, HorizontalOptions = LayoutOptions.End };

    public SpeciesCell(ShinyHuntPage page, Action<int> picked, Func<int> selected)
    {
        _page = page;
        _selected = selected;
        StrokeThickness = 0;
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 };
        Padding = new Thickness(18, 6, 12, 6);
        Margin = new Thickness(0, 4);
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        grid.Add(new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { _name, _number } });
        Grid.SetColumn(_image, 1);
        grid.Add(_image);
        Content = grid;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (BindingContext is SpeciesRow row) picked(row.Id);
        };
        GestureRecognizers.Add(tap);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (BindingContext is not SpeciesRow row) return;
        _name.Text = row.Name.ToUpperInvariant();
        _number.Text = $"#{row.Id:000}";
        BackgroundColor = row.Id == _selected() ? Color.FromArgb("#3b3f66") : ShinyUi.Card;
        _image.ShowShiny(_page.Sprites, row.Id);
    }
}

/// <summary>A row of the games list.</summary>
internal sealed class GameCell : Border
{
    private readonly Label _name = ShinyUi.Txt("", 21, ShinyUi.AccentText, bold: true);
    private readonly Image _icon = new() { HeightRequest = 52, WidthRequest = 52, Aspect = Aspect.AspectFit, HorizontalOptions = LayoutOptions.End };
    private readonly Func<string?> _selected;

    public GameCell(Action<string> picked, Func<string?> selected)
    {
        _selected = selected;
        StrokeThickness = 0;
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 };
        Padding = new Thickness(18, 10, 14, 10);
        Margin = new Thickness(0, 4);
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        grid.Add(_name);
        Grid.SetColumn(_icon, 1);
        grid.Add(_icon);
        Content = grid;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (BindingContext is HuntGame game) picked(game.Id);
        };
        GestureRecognizers.Add(tap);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (BindingContext is not HuntGame game) return;
        _name.Text = game.Name.ToUpperInvariant();
        _icon.Source = GameIcons.For(game.Icon);
        BackgroundColor = game.Id == _selected() ? Color.FromArgb("#3b3f66") : ShinyUi.Card;
    }
}

/// <summary>
/// The pop-up that starts a hunt (or changes one): Pokémon, game, method and settings in four tabs, like the reference site.
/// The method list only holds the methods the chosen game has.
/// </summary>
internal sealed class ShinyCreateDialog : ContentView
{
    private readonly ShinyHuntPage _page;
    private readonly Hunt? _editing;
    private readonly Action _done;
    private readonly List<SpeciesRow> _species;
    private readonly Button[] _rail = new Button[4];
    private readonly Label _title = ShinyUi.Txt("", 24, bold: true);
    private readonly ContentView _content = new();
    private readonly Button _create;
    private Label _preview = ShinyUi.Txt("", 17, ShinyUi.AccentText);

    private int _speciesId;
    private string? _gameId;
    private string? _methodId;
    private bool _charm;
    private Dictionary<string, int> _choices = new();
    private bool _byName;

    private static readonly string[] Titles = ["CHOISIR UN POKÉMON", "CHOISIR UN JEU", "CHOISIR UNE MÉTHODE", "RÉGLAGES"];
    private static readonly string[] Glyphs = ["◉", "▣", "◈", "⚙"];

    public ShinyCreateDialog(ShinyHuntPage page, Hunt? editing, Action done)
    {
        _page = page;
        _editing = editing;
        _done = done;
        _species = [.. Enumerable.Range(1, CryCatalog.LastSpecies)
            .Select(id => new SpeciesRow(id, ShinyUi.SpeciesName(page.Data, id)))
            .Where(row => !row.Name.StartsWith('#'))];
        if (editing is not null)
        {
            _speciesId = editing.Species;
            _gameId = editing.GameId;
            _methodId = editing.MethodId;
            _charm = editing.Charm;
            _choices = new Dictionary<string, int>(editing.Choices);
        }

        var rail = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(8, 14) };
        for (var i = 0; i < _rail.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Text = Glyphs[i],
                FontSize = 24,
                TextColor = Colors.White,
                BackgroundColor = ShinyUi.Field,
                BorderColor = ShinyUi.Accent,
                BorderWidth = 0,
                CornerRadius = 10,
                WidthRequest = 56,
                HeightRequest = 58,
                Padding = 0,
            };
            button.Clicked += (_, _) => ShowTab(index);
            _rail[i] = button;
            rail.Add(button);
        }

        _create = new Button
        {
            Text = "+",
            FontSize = 34,
            TextColor = Colors.White,
            CornerRadius = 10,
            WidthRequest = 62,
            HeightRequest = 62,
            Padding = 0,
            Margin = new Thickness(8, 8, 8, 12),
            HorizontalOptions = LayoutOptions.Center,
        };
        _create.Clicked += (_, _) => Create();

        var left = new Grid
        {
            BackgroundColor = Color.FromArgb("#2a2b30"),
            RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
        };
        left.Add(rail);
        Grid.SetRow(_create, 1);
        left.Add(_create);

        var close = new Button
        {
            Text = "✕",
            FontSize = 22,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 48,
            HeightRequest = 48,
            Padding = 0,
        };
        close.Clicked += (_, _) => _page.HideOverlay();
        var head = new Grid
        {
            Padding = new Thickness(18, 14, 8, 4),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        head.Add(_title);
        Grid.SetColumn(close, 1);
        head.Add(close);

        var right = new Grid { BackgroundColor = Color.FromArgb("#1b1c20"), RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        right.Add(head);
        Grid.SetRow(_content, 1);
        right.Add(_content);

        var all = new Grid { ColumnDefinitions = { new ColumnDefinition(new GridLength(72)), new ColumnDefinition(GridLength.Star) } };
        all.Add(left);
        Grid.SetColumn(right, 1);
        all.Add(right);
        Content = new Border
        {
            Margin = new Thickness(10, 36, 10, 16),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            BackgroundColor = Color.FromArgb("#1b1c20"),
            Content = all,
        };
        ShowTab(editing is null ? 0 : 3);
    }

    private HuntGame? Game => _gameId is null ? null : ShinyOdds.Game(_gameId);

    private HuntMethod? Method => Game is { } game && _methodId is not null ? ShinyOdds.Method(game, _methodId) : null;

    private bool Ready => _speciesId > 0 && Method is not null;

    private void ShowTab(int index)
    {
        for (var i = 0; i < _rail.Length; i++) _rail[i].BorderWidth = i == index ? 3 : 0;
        _title.Text = Titles[index];
        _content.Content = index switch
        {
            0 => BuildSpecies(),
            1 => BuildGames(),
            2 => BuildMethods(),
            _ => BuildSettings(),
        };
        _create.BackgroundColor = Ready ? ShinyUi.Accent : Color.FromArgb("#6b6c70");
        _create.IsEnabled = Ready;
    }

    // ── Pokémon ──────────────────────────────────────────────────────────────

    private View BuildSpecies()
    {
        var query = new Entry
        {
            Placeholder = "Rechercher…",
            FontFamily = ShinyUi.FontName,
            TextColor = Colors.White,
            PlaceholderColor = ShinyUi.Muted,
            BackgroundColor = Colors.Transparent,
        };
        var list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() => new SpeciesCell(_page, PickSpecies, () => _speciesId)),
            ItemsSource = SpeciesList(""),
            Margin = new Thickness(18, 0),
        };
        query.TextChanged += (_, _) => list.ItemsSource = SpeciesList(query.Text);
        var sort = ShinyUi.Round("⇅", 46, filled: true);
        sort.Clicked += (_, _) =>
        {
            _byName = !_byName;
            list.ItemsSource = SpeciesList(query.Text);
        };
        var shuffle = ShinyUi.Round("⤮", 46, filled: true);
        shuffle.Clicked += (_, _) => PickSpecies(_species[System.Random.Shared.Next(_species.Count)].Id);

        var tools = new HorizontalStackLayout { Spacing = 12, Padding = new Thickness(18, 4) };
        tools.Add(sort);
        tools.Add(shuffle);
        var box = new Border
        {
            Stroke = ShinyUi.Accent,
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 23 },
            Padding = new Thickness(14, 0),
            HeightRequest = 46,
            Margin = new Thickness(18, 4, 18, 8),
            Content = query,
        };
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        grid.Add(tools);
        Grid.SetRow(box, 1);
        grid.Add(box);
        Grid.SetRow(list, 2);
        grid.Add(list);
        return grid;
    }

    private List<SpeciesRow> SpeciesList(string? text)
    {
        IEnumerable<SpeciesRow> rows = _species;
        var query = text?.Trim() ?? "";
        if (query.Length > 0)
            rows = rows.Where(r => r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                   || r.Id.ToString().Contains(query, StringComparison.Ordinal));
        if (_byName) rows = rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
        return [.. rows];
    }

    private void PickSpecies(int id)
    {
        _speciesId = id;
        ShowTab(_gameId is null ? 1 : 0);
    }

    // ── Game ─────────────────────────────────────────────────────────────────

    private View BuildGames()
    {
        var query = new Entry
        {
            Placeholder = "Rechercher…",
            FontFamily = ShinyUi.FontName,
            TextColor = Colors.White,
            PlaceholderColor = ShinyUi.Muted,
            BackgroundColor = Colors.Transparent,
        };
        var list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() => new GameCell(PickGame, () => _gameId)),
            ItemsSource = GameList(""),
            Margin = new Thickness(18, 0),
        };
        query.TextChanged += (_, _) => list.ItemsSource = GameList(query.Text);
        var box = new Border
        {
            Stroke = ShinyUi.Accent,
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 23 },
            Padding = new Thickness(14, 0),
            HeightRequest = 46,
            Margin = new Thickness(18, 4, 18, 8),
            Content = query,
        };
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        grid.Add(box);
        Grid.SetRow(list, 1);
        grid.Add(list);
        return grid;
    }

    private static List<HuntGame> GameList(string? text)
    {
        var query = text?.Trim() ?? "";
        return [.. ShinyOdds.Games.Where(g => query.Length == 0 || g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))];
    }

    private void PickGame(string id)
    {
        _gameId = id;
        var game = Game;
        // A method the new game does not have is dropped; so is a charm it cannot use.
        if (game is not null && ShinyOdds.Method(game, _methodId ?? "") is null)
        {
            _methodId = null;
            _choices = new();
        }
        if (game is not null && !game.HasCharm) _charm = false;
        ShowTab(_methodId is null ? 2 : 1);
    }

    // ── Method ───────────────────────────────────────────────────────────────

    private View BuildMethods()
    {
        if (Game is not { } game)
            return ShinyUi.Txt("Choisis d'abord un jeu : les méthodes dépendent du jeu.", 18, ShinyUi.Muted, align: TextAlignment.Center);
        var stack = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(18, 8, 18, 24) };
        foreach (var method in game.Methods)
        {
            var current = method;
            var badge = new Border
            {
                BackgroundColor = ShinyUi.Accent,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
                WidthRequest = 44,
                HeightRequest = 44,
                Content = ShinyUi.Txt(method.Abbr, 18, bold: true, align: TextAlignment.Center),
            };
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            row.Add(ShinyUi.Txt(method.Name.ToUpperInvariant(), 21, ShinyUi.AccentText, bold: true));
            Grid.SetColumn(badge, 1);
            row.Add(badge);
            var card = new Border
            {
                BackgroundColor = method.Id == _methodId ? Color.FromArgb("#3b3f66") : ShinyUi.Card,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
                Padding = new Thickness(18, 14, 14, 14),
                Content = row,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => PickMethod(current);
            card.GestureRecognizers.Add(tap);
            stack.Add(card);
        }
        return new ScrollView { Content = stack };
    }

    private void PickMethod(HuntMethod method)
    {
        if (_methodId != method.Id) _choices = new();
        _methodId = method.Id;
        ShowTab(3);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private View BuildSettings()
    {
        if (Method is not { } method || Game is not { } game)
            return ShinyUi.Txt("Choisis un jeu et une méthode pour régler la chasse.", 18, ShinyUi.Muted, align: TextAlignment.Center);

        _preview = ShinyUi.Txt("", 17, ShinyUi.AccentText);
        var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(18, 8, 18, 24) };
        stack.Add(ShinyUi.Txt($"{ShinyUi.SpeciesName(_page.Data, _speciesId)} · {game.Name} · {method.Name}", 16, ShinyUi.Muted));
        if (game.HasCharm && method.CharmApplies)
        {
            var toggle = new Switch { IsToggled = _charm, OnColor = ShinyUi.Accent, VerticalOptions = LayoutOptions.Center };
            toggle.Toggled += (_, args) =>
            {
                _charm = args.Value;
                UpdatePreview();
            };
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            row.Add(ShinyUi.Txt("Charme Chroma", 20));
            Grid.SetColumn(toggle, 1);
            row.Add(toggle);
            stack.Add(row);
        }
        foreach (var group in ShinyOdds.OptionsOf(method))
        {
            stack.Add(ShinyUi.Txt(group.Label, 20));
            var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            var buttons = new List<Button>();
            for (var i = 0; i < group.Choices.Length; i++)
            {
                var index = i;
                var chip = new Button
                {
                    Text = group.Choices[i].Label,
                    FontFamily = ShinyUi.FontName,
                    FontSize = 16,
                    CornerRadius = 18,
                    HeightRequest = 38,
                    Padding = new Thickness(16, 0),
                    Margin = new Thickness(0, 0, 8, 8),
                };
                chip.Clicked += (_, _) =>
                {
                    _choices[group.Id] = index;
                    Highlight(buttons, index);
                    UpdatePreview();
                };
                buttons.Add(chip);
                chips.Add(chip);
            }
            Highlight(buttons, Math.Clamp(_choices.GetValueOrDefault(group.Id), 0, group.Choices.Length - 1));
            stack.Add(chips);
        }
        stack.Add(_preview);
        UpdatePreview();
        return new ScrollView { Content = stack };
    }

    private static void Highlight(List<Button> chips, int selected)
    {
        for (var i = 0; i < chips.Count; i++)
        {
            chips[i].BackgroundColor = i == selected ? ShinyUi.Accent : ShinyUi.Card;
            chips[i].TextColor = i == selected ? Colors.White : ShinyUi.Muted;
        }
    }

    private void UpdatePreview()
    {
        if (Game is not { } game || Method is not { } method) return;
        var odds = ShinyOdds.Compute(game, method, _charm, _choices);
        _preview.Text = $"Taux actuel : {odds.Current}";
    }

    // ── Create ───────────────────────────────────────────────────────────────

    private void Create()
    {
        if (!Ready || _gameId is null || _methodId is null) return;
        var name = ShinyUi.SpeciesName(_page.Data, _speciesId);
        if (_editing is { } hunt)
        {
            hunt.Settle();
            if (hunt.Species != _speciesId) hunt.Name = name;
            hunt.Species = _speciesId;
            hunt.GameId = _gameId;
            hunt.MethodId = _methodId;
            hunt.Charm = _charm;
            hunt.Choices = new Dictionary<string, int>(_choices);
            _page.Store.Save();
        }
        else
        {
            _page.Store.Add(new Hunt
            {
                Species = _speciesId,
                Name = name,
                GameId = _gameId,
                MethodId = _methodId,
                Charm = _charm,
                Choices = new Dictionary<string, int>(_choices),
            });
        }
        _done();
    }
}
