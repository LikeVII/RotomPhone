using PKForge.App.Services;

namespace PKForge.App.Views;

/// <summary>The shinies that were caught: a list of cards, and the detail of one.</summary>
public sealed class ShinyCollectionView : ContentView
{
    private readonly ShinyHuntPage _page;
    private readonly Entry _search;
    private readonly VerticalStackLayout _list = new() { Spacing = 16, Padding = new Thickness(16, 6, 16, 28) };
    private readonly Grid _listRoot;
    private ShinyCatch? _detail;
    private int _sort;

    private static readonly string[] SortNames = ["Plus récents d'abord", "Favoris d'abord", "Par nom", "Par rencontres"];

    public ShinyCollectionView(ShinyHuntPage page)
    {
        _page = page;
        _search = new Entry
        {
            Placeholder = "Rechercher…",
            FontFamily = ShinyUi.FontName,
            TextColor = Colors.White,
            PlaceholderColor = ShinyUi.Muted,
            BackgroundColor = Colors.Transparent,
        };
        _search.TextChanged += (_, _) => Rebuild();
        var box = new Border
        {
            Stroke = ShinyUi.Accent,
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 23 },
            Padding = new Thickness(14, 0),
            HeightRequest = 46,
            Content = _search,
        };
        var sort = ShinyUi.Round("⇅");
        sort.Clicked += async (_, _) =>
        {
            var choice = await _page.ChooseAsync("Trier la collection", SortNames);
            var index = choice is null ? -1 : Array.IndexOf(SortNames, choice);
            if (index < 0) return;
            _sort = index;
            Rebuild();
        };
        var tools = new Grid
        {
            Padding = new Thickness(16, 12, 16, 10),
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
        };
        tools.Add(sort);
        Grid.SetColumn(box, 1);
        tools.Add(box);
        _listRoot = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        _listRoot.Add(tools);
        var scroll = new ScrollView { Content = _list };
        Grid.SetRow(scroll, 1);
        _listRoot.Add(scroll);
    }

    public void Rebuild()
    {
        if (_detail is not null && !_page.Store.Catches.Contains(_detail)) _detail = null;
        if (_detail is { } caught)
        {
            Content = BuildDetail(caught);
            return;
        }
        Content = _listRoot;
        _list.Children.Clear();
        var query = _search.Text?.Trim() ?? "";
        IEnumerable<ShinyCatch> items = _page.Store.Catches;
        if (query.Length > 0)
            items = items.Where(c => c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                     || ShinyUi.SpeciesName(_page.Data, c.Species).Contains(query, StringComparison.CurrentCultureIgnoreCase));
        items = _sort switch
        {
            1 => items.OrderByDescending(c => c.Favorite).ThenByDescending(c => c.CaughtUtc),
            2 => items.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            3 => items.OrderByDescending(c => c.Encounters),
            _ => items.OrderByDescending(c => c.CaughtUtc),
        };
        var any = false;
        foreach (var item in items)
        {
            any = true;
            _list.Add(BuildCard(item, big: false));
        }
        if (!any)
            _list.Add(ShinyUi.Txt(_page.Store.Catches.Count == 0
                ? "Ta collection est vide. Quand tu attrapes un chromatique, touche la cible ◎ de sa chasse."
                : "Aucun chromatique ne correspond à la recherche.", 17, ShinyUi.Muted, align: TextAlignment.Center));
    }

    // ── Cards ────────────────────────────────────────────────────────────────

    private View BuildCard(ShinyCatch item, bool big)
    {
        var size = big ? 300.0 : 150.0;
        var ball = new SpriteImage { HeightRequest = size, WidthRequest = size, Opacity = 0.9 };
        ball.ShowBall(_page.Sprites, item.Ball);
        var mon = new SpriteImage { HeightRequest = size * 0.9, WidthRequest = size * 0.9 };
        mon.ShowShiny(_page.Sprites, item.Species);
        var picture = new Grid { HeightRequest = size, WidthRequest = size, HorizontalOptions = LayoutOptions.Center };
        picture.Add(ball);
        picture.Add(mon);

        var heart = new Button
        {
            Text = "♥",
            FontSize = 18,
            TextColor = item.Favorite ? Color.FromArgb("#ff6f91") : Colors.White,
            BackgroundColor = Color.FromArgb("#70757d"),
            CornerRadius = 19,
            WidthRequest = 38,
            HeightRequest = 38,
            Padding = 0,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
        };
        heart.Clicked += (_, _) =>
        {
            item.Favorite = !item.Favorite;
            _page.Store.Save();
            Rebuild();
        };

        var name = ShinyUi.Txt(big ? item.Name.ToUpperInvariant() : item.Name, big ? 34 : 28, bold: true);
        var time = ShinyUi.Txt(ShinyUi.Duration(item.TotalMs), big ? 26 : 17, bold: big);
        var totalCaption = big ? ShinyUi.Txt("temps total", 20) : null;
        var count = ShinyUi.Txt(item.Encounters.ToString(), big ? 64 : 44, bold: true, align: TextAlignment.End);
        var countCaption = ShinyUi.Txt("rencontres", big ? 20 : 17, align: TextAlignment.End);

        var grid = new Grid { Padding = new Thickness(16, 12) };
        if (big)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.Add(name);
            Grid.SetColumn(heart, 1);
            grid.Add(heart);
            Grid.SetRow(picture, 1);
            Grid.SetColumnSpan(picture, 2);
            grid.Add(picture);
            var bottom = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            bottom.Add(new VerticalStackLayout { Spacing = 0, Children = { time, totalCaption! } });
            var right = new VerticalStackLayout { Spacing = 0, Children = { count, countCaption } };
            Grid.SetColumn(right, 1);
            bottom.Add(right);
            Grid.SetRow(bottom, 2);
            Grid.SetColumnSpan(bottom, 2);
            grid.Add(bottom);
        }
        else
        {
            grid.HeightRequest = 166;
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var left = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Start, Children = { name, time } };
            grid.Add(left);
            Grid.SetColumn(picture, 1);
            grid.Add(picture);
            var right = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
            right.Add(heart);
            var numbers = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.End, Children = { count, countCaption } };
            Grid.SetRow(numbers, 1);
            right.Add(numbers);
            Grid.SetColumn(right, 2);
            grid.Add(right);
        }

        var card = new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection { new GradientStop(Color.FromArgb("#000000"), 0f), new GradientStop(Color.FromArgb("#2c1740"), 1f) },
                new Point(0, 0), new Point(1, 1)),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
            Content = grid,
        };
        if (!big)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _detail = item;
                Rebuild();
            };
            card.GestureRecognizers.Add(tap);
        }
        return card;
    }

    // ── Detail ───────────────────────────────────────────────────────────────

    private View BuildDetail(ShinyCatch item)
    {
        var back = ShinyUi.Round("←");
        back.Clicked += (_, _) =>
        {
            _detail = null;
            Rebuild();
        };
        var edit = ShinyUi.Round("✎");
        edit.Clicked += async (_, _) => await EditAsync(item);
        var delete = ShinyUi.Round("🗑");
        delete.HorizontalOptions = LayoutOptions.End;
        delete.Clicked += async (_, _) =>
        {
            if (!await _page.ConfirmAsync("Supprimer ?", $"{item.Name} sera retiré de ta collection.", "Supprimer")) return;
            _page.Store.Remove(item);
            _detail = null;
            Rebuild();
        };
        var tools = new Grid
        {
            Padding = new Thickness(16, 12, 16, 10),
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
        };
        tools.Add(back);
        Grid.SetColumn(edit, 1);
        tools.Add(edit);
        Grid.SetColumn(delete, 2);
        tools.Add(delete);

        var game = ShinyOdds.Game(item.GameId);
        var method = game is null ? null : ShinyOdds.Method(game, item.MethodId);
        var details = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(20, 18) };
        details.Add(ShinyUi.Txt("DÉTAILS DE LA CHASSE", 17, ShinyUi.AccentText, bold: true));
        details.Add(Line("Pokémon", ShinyUi.SpeciesName(_page.Data, item.Species)));
        details.Add(Line("Jeu", game?.Name ?? "?"));
        details.Add(Line("Méthode", method?.Name ?? "?"));
        details.Add(Line("Date de capture", ShinyUi.Date(item.CaughtUtc)));
        details.Add(Line("Poké Ball", ShinyUi.BallName(_page.Data, item.Ball)));
        var settings = new List<string>();
        if (item.Charm) settings.Add("Charme Chroma");
        foreach (var line in ShinyHuntText.OptionLines(method, item.Choices))
            settings.Add($"{line.Label} : {line.Value}");
        if (settings.Count > 0)
        {
            details.Add(ShinyUi.Txt("RÉGLAGES DE LA CHASSE", 17, ShinyUi.AccentText, bold: true));
            foreach (var setting in settings) details.Add(Line(setting, ""));
        }
        if (item.Description.Length > 0)
        {
            details.Add(ShinyUi.Txt("DESCRIPTION", 17, ShinyUi.AccentText, bold: true));
            details.Add(ShinyUi.Txt(item.Description, 17));
        }

        var stack = new VerticalStackLayout { Spacing = 0, Padding = new Thickness(16, 0, 16, 28) };
        stack.Add(BuildCard(item, big: true));
        stack.Add(new Border
        {
            BackgroundColor = ShinyUi.Panel,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            Margin = new Thickness(0, 12, 0, 0),
            Content = details,
        });
        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        root.Add(tools);
        var scroll = new ScrollView { Content = stack };
        Grid.SetRow(scroll, 1);
        root.Add(scroll);
        return root;
    }

    private static View Line(string left, string right)
    {
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        row.Add(ShinyUi.Txt(left, 18));
        var value = ShinyUi.Txt(right, 18, align: TextAlignment.End);
        Grid.SetColumn(value, 1);
        row.Add(value);
        var holder = new VerticalStackLayout { Spacing = 6 };
        holder.Add(row);
        holder.Add(ShinyUi.Line());
        return holder;
    }

    private async Task EditAsync(ShinyCatch item)
    {
        var choice = await _page.ChooseAsync("Modifier", "Surnom", "Rencontres", "Temps total", "Poké Ball", "Description");
        switch (choice)
        {
            case "Surnom":
                var name = await _page.AskAsync("Surnom", "Comment s'appelle ce chromatique ?", item.Name, false);
                if (!string.IsNullOrWhiteSpace(name)) item.Name = name.Trim();
                break;
            case "Rencontres":
                var count = await _page.AskAsync("Rencontres", "Nombre de rencontres.", item.Encounters.ToString(), true);
                if (int.TryParse(count, out var encounters) && encounters >= 0) item.Encounters = encounters;
                break;
            case "Temps total":
                var time = await _page.AskAsync("Temps total", "Durée de la chasse (h:mm:ss).", ShinyUi.Clock(item.TotalMs), false);
                if (ShinyUi.ParseDuration(time) is { } milliseconds) item.TotalMs = milliseconds;
                break;
            case "Poké Ball":
                if (await BallPicker.ChooseAsync(_page, item.Ball) is { } ball) item.Ball = ball;
                break;
            case "Description":
                var text = await _page.AskAsync("Description", "Un petit mot sur cette capture.", item.Description, false);
                if (text is not null) item.Description = text.Trim();
                break;
            default:
                return;
        }
        _page.Store.Save();
        Rebuild();
    }
}

/// <summary>A cell of the ShinyDex: the Pokémon when its shiny was caught, only its number otherwise.</summary>
internal sealed class DexCell : VerticalStackLayout
{
    private readonly ShinyHuntPage _page;
    private readonly Func<int, bool> _owned;
    private readonly SpriteImage _image = new() { HeightRequest = 78, WidthRequest = 78, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _number = ShinyUi.Txt("", 13, ShinyUi.Muted, align: TextAlignment.Center);

    public DexCell(ShinyHuntPage page, Func<int, bool> owned)
    {
        _page = page;
        _owned = owned;
        Spacing = 0;
        Add(_image);
        Add(_number);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (BindingContext is not SpeciesRow row) return;
        var owned = _owned(row.Id);
        _number.Text = $"#{row.Id:000}";
        _number.TextColor = owned ? Colors.White : ShinyUi.Muted;
        if (owned) _image.ShowShiny(_page.Sprites, row.Id);
        else _image.Clear();
    }
}

/// <summary>Every Pokémon, lit when a shiny of it is in the collection.</summary>
public sealed class ShinyDexView : ContentView
{
    private readonly ShinyHuntPage _page;
    private readonly Label _summary = ShinyUi.Txt("", 20, bold: true, align: TextAlignment.Center);
    private readonly CollectionView _grid;
    private HashSet<int> _owned = [];

    public ShinyDexView(ShinyHuntPage page)
    {
        _page = page;
        _grid = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemsLayout = new GridItemsLayout(4, ItemsLayoutOrientation.Vertical) { VerticalItemSpacing = 10, HorizontalItemSpacing = 4 },
            ItemTemplate = new DataTemplate(() => new DexCell(_page, id => _owned.Contains(id))),
            Margin = new Thickness(12, 0),
        };
        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        _summary.Margin = new Thickness(0, 16, 0, 12);
        root.Add(_summary);
        Grid.SetRow(_grid, 1);
        root.Add(_grid);
        Content = root;
    }

    public void Rebuild()
    {
        _owned = [.. _page.Store.Catches.Select(c => c.Species)];
        _summary.Text = $"Chromatiques : {_owned.Count} / {CryCatalog.LastSpecies}";
        _grid.ItemsSource = Enumerable.Range(1, CryCatalog.LastSpecies).Select(id => new SpeciesRow(id, "")).ToList();
    }
}
