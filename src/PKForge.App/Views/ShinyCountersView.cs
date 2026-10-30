using PKForge.App.Services;

namespace PKForge.App.Views;

/// <summary>The list of hunts in progress, with search, sort and the button that starts a new one.</summary>
public sealed class ShinyCountersView : ContentView
{
    private readonly ShinyHuntPage _page;
    private readonly VerticalStackLayout _list = new() { Spacing = 18, Padding = new Thickness(16, 6, 16, 28) };
    private readonly Entry _search;
    private readonly List<HuntCard> _cards = [];
    private int _sort;

    private static readonly string[] SortNames = ["Plus récentes d'abord", "Par nom", "Par rencontres"];

    public ShinyCountersView(ShinyHuntPage page)
    {
        _page = page;
        _search = new Entry
        {
            Placeholder = "Rechercher…",
            FontFamily = ShinyUi.FontName,
            TextColor = Colors.White,
            PlaceholderColor = ShinyUi.Muted,
            BackgroundColor = Colors.Transparent,
            VerticalOptions = LayoutOptions.Center,
        };
        _search.TextChanged += (_, _) => Rebuild();
        var searchBox = new Border
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
            var choice = await _page.ChooseAsync("Trier les chasses", SortNames);
            var index = choice is null ? -1 : Array.IndexOf(SortNames, choice);
            if (index < 0) return;
            _sort = index;
            Rebuild();
        };
        var add = ShinyUi.Round("+");
        add.Clicked += (_, _) => _page.ShowCreate(null);

        var tools = new Grid
        {
            Padding = new Thickness(16, 12, 16, 10),
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star),
            },
        };
        tools.Add(sort);
        Grid.SetColumn(add, 1);
        tools.Add(add);
        Grid.SetColumn(searchBox, 2);
        tools.Add(searchBox);

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        root.Add(tools);
        var scroll = new ScrollView { Content = _list };
        Grid.SetRow(scroll, 1);
        root.Add(scroll);
        Content = root;
    }

    public void Rebuild()
    {
        _list.Children.Clear();
        _cards.Clear();
        var query = _search.Text?.Trim() ?? "";
        IEnumerable<Hunt> hunts = _page.Store.Hunts;
        if (query.Length > 0)
            hunts = hunts.Where(h => h.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        hunts = _sort switch
        {
            1 => hunts.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => hunts.OrderByDescending(h => h.Encounters),
            _ => hunts.OrderByDescending(h => h.CreatedUtc),
        };
        foreach (var hunt in hunts)
        {
            var card = new HuntCard(_page, hunt, Rebuild);
            _cards.Add(card);
            _list.Add(card);
        }
        if (_cards.Count == 0)
            _list.Add(ShinyUi.Txt(_page.Store.Hunts.Count == 0
                ? "Aucune chasse pour le moment. Touche + pour en commencer une."
                : "Aucune chasse ne correspond à la recherche.", 17, ShinyUi.Muted, align: TextAlignment.Center));
    }

    /// <summary>Once a second: the running timers.</summary>
    public void Tick()
    {
        foreach (var card in _cards) card.Tick();
    }
}

/// <summary>One counter: the Pokémon, its count, timers, play/pause and the panels (odds, caught, settings).</summary>
internal sealed class HuntCard : ContentView
{
    private readonly ShinyHuntPage _page;
    private readonly Hunt _hunt;
    private readonly Action _changed;
    private readonly Label _title = ShinyUi.Txt("", 34, bold: true);
    private readonly Label _total = ShinyUi.Txt("", 19);
    private readonly Label _session = ShinyUi.Txt("", 19);
    private readonly Label _count = ShinyUi.Txt("0", 64, bold: true, align: TextAlignment.Center);
    private readonly Label _status = ShinyUi.Txt("", 15, ShinyUi.Muted, align: TextAlignment.Center);
    private readonly SpriteImage _mon = new() { HeightRequest = 200, HorizontalOptions = LayoutOptions.Center };
    private readonly Button _play;
    private readonly VerticalStackLayout _panel = new() { Spacing = 10, Padding = new Thickness(20, 16), BackgroundColor = ShinyUi.Panel, IsVisible = false };

    // What the player may change before the shiny is saved to the collection.
    private string _draftName = "";
    private int _draftBall = 4;
    private string _draftDescription = "";

    public HuntCard(ShinyHuntPage page, Hunt hunt, Action changed)
    {
        _page = page;
        _hunt = hunt;
        _changed = changed;

        var menu = new Button
        {
            Text = "⋮",
            FontSize = 26,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 48,
            HeightRequest = 48,
            Padding = 0,
            VerticalOptions = LayoutOptions.Start,
        };
        menu.Clicked += (_, _) => ShowSettings();
        var names = new VerticalStackLayout { Spacing = 2, Children = { _title, _total, _session } };
        var header = new Grid
        {
            Padding = new Thickness(18, 8, 6, 0),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        header.Add(names);
        Grid.SetColumn(menu, 1);
        header.Add(menu);

        var plaque = new Border
        {
            BackgroundColor = Colors.Black,
            Stroke = ShinyUi.Gold,
            StrokeThickness = 3,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(40, 0),
            HorizontalOptions = LayoutOptions.Center,
            Content = _count,
        };
        var reset = ShinyUi.Round("↺", 34, color: Colors.White);
        reset.BorderWidth = 0;
        reset.Clicked += async (_, _) => await ResetAsync();
        var plaqueRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
        };
        Grid.SetColumn(plaque, 1);
        plaqueRow.Add(plaque);
        Grid.SetColumn(reset, 2);
        reset.HorizontalOptions = LayoutOptions.Start;
        reset.VerticalOptions = LayoutOptions.End;
        plaqueRow.Add(reset);

        var tapArea = new VerticalStackLayout { Spacing = 4, Children = { _mon } };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => CountOne();
        tapArea.GestureRecognizers.Add(tap);

        var odds = ShinyUi.Round("∿", 44, color: Colors.White);
        odds.BorderWidth = 0;
        odds.Clicked += (_, _) => ShowOdds();
        _play = new Button
        {
            Text = "▶",
            FontSize = 24,
            TextColor = Colors.Black,
            BackgroundColor = Colors.White,
            CornerRadius = 28,
            WidthRequest = 56,
            HeightRequest = 56,
            Padding = 0,
            HorizontalOptions = LayoutOptions.Center,
        };
        _play.Clicked += (_, _) => TogglePlay();
        var caught = ShinyUi.Round("◎", 44, color: Colors.White);
        caught.BorderWidth = 0;
        caught.Clicked += (_, _) => ShowCaught();
        var actions = new Grid
        {
            Padding = new Thickness(18, 6, 18, 14),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
        };
        odds.HorizontalOptions = LayoutOptions.Start;
        caught.HorizontalOptions = LayoutOptions.End;
        actions.Add(odds);
        Grid.SetColumn(_play, 1);
        actions.Add(_play);
        Grid.SetColumn(caught, 2);
        actions.Add(caught);

        var column = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { header, tapArea, plaqueRow, _status, _panel, actions },
        };
        Content = new Border
        {
            BackgroundColor = ShinyUi.Card,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 },
            Content = column,
        };

        var look = _hunt.Species;
        _mon.ShowShiny(_page.Sprites, look);
        Refresh();
    }

    private HuntGame? Game => ShinyOdds.Game(_hunt.GameId);

    private HuntMethod? Method => Game is { } game ? ShinyOdds.Method(game, _hunt.MethodId) : null;

    public void Refresh()
    {
        _title.Text = _hunt.Name;
        _count.Text = _hunt.Encounters.ToString();
        _play.Text = _hunt.Running ? "⏸" : "▶";
        _status.Text = _hunt.Running
            ? "en cours · touche le Pokémon pour compter"
            : _hunt.TotalMs > 0 ? "en pause" : "appuie sur lecture pour commencer";
        Tick();
    }

    public void Tick()
    {
        _total.Text = ShinyUi.Duration(_hunt.TotalNowMs);
        _session.Text = ShinyUi.Duration(_hunt.SessionNowMs);
    }

    private void TogglePlay()
    {
        if (_hunt.Running) _hunt.Pause();
        else _hunt.Start();
        _page.Store.Save();
        Refresh();
    }

    private void CountOne()
    {
        if (!_hunt.Running) return;
        _hunt.Settle();
        _hunt.SessionMs = 0;
        _hunt.Encounters += Math.Max(1, _hunt.Increment);
        _page.Store.Save();
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch (Exception) { /* no vibrator */ }
        Refresh();
    }

    private async Task ResetAsync()
    {
        if (!await _page.ConfirmAsync("Remettre à zéro ?", $"Le compteur de {_hunt.Name} repart de 0.", "Remettre à zéro")) return;
        _hunt.Settle();
        _hunt.Encounters = 0;
        _hunt.SessionMs = 0;
        _page.Store.Save();
        Refresh();
    }

    // ── Panels ───────────────────────────────────────────────────────────────

    private void OpenPanel(string title)
    {
        _panel.Children.Clear();
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        header.Add(ShinyUi.Txt(title, 19, bold: true));
        var close = new Button
        {
            Text = "✕",
            FontSize = 18,
            TextColor = ShinyUi.AccentText,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 44,
            HeightRequest = 36,
            Padding = 0,
        };
        close.Clicked += (_, _) => _panel.IsVisible = false;
        Grid.SetColumn(close, 1);
        header.Add(close);
        _panel.Add(header);
        _panel.IsVisible = true;
    }

    private static View Row(string left, string right, bool bold = false)
    {
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        row.Add(ShinyUi.Txt(left, 18, bold: bold));
        var value = ShinyUi.Txt(right, 18, bold: bold, align: TextAlignment.End);
        Grid.SetColumn(value, 1);
        row.Add(value);
        return row;
    }

    private void ShowOdds()
    {
        OpenPanel("CHANCES CHROMATIQUES");
        if (Game is not { } game || Method is not { } method)
        {
            _panel.Add(ShinyUi.Txt("Le jeu ou la méthode de cette chasse n'existe plus.", 17, ShinyUi.Muted));
            return;
        }
        var odds = ShinyOdds.Compute(game, method, _hunt.Charm, _hunt.Choices);
        _panel.Add(Row("Taux de base", odds.Base));
        _panel.Add(Row("Relances du Charme Chroma", odds.Fixed ? "—" : $"+{odds.CharmRolls}"));
        _panel.Add(Row("Relances de la méthode", odds.Fixed ? "—" : $"+{odds.MethodRolls}"));
        _panel.Add(new BoxView { HeightRequest = 2, Color = Color.FromArgb("#3a3b40") });
        _panel.Add(Row("Taux actuel", odds.Current, bold: true));
        if (_hunt.Encounters > 0)
            _panel.Add(Row("Chance de l'avoir déjà trouvé", $"{ShinyOdds.ChanceAfter(odds.Probability, _hunt.Encounters):0.#} %"));
    }

    private void ShowCaught()
    {
        if (_draftName.Length == 0) _draftName = _hunt.Name;
        OpenPanel("FÉLICITATIONS !");
        _panel.Add(Row("Pokémon", _hunt.Name));
        _panel.Add(Row("Rencontres", _hunt.Encounters.ToString()));
        _panel.Add(Row("Temps total", ShinyUi.Duration(_hunt.TotalNowMs)));
        _panel.Add(new BoxView { HeightRequest = 2, Color = Color.FromArgb("#3a3b40") });
        var edit = new Button { Text = "✎", FontSize = 22, TextColor = ShinyUi.AccentText, BackgroundColor = Colors.Transparent, WidthRequest = 48, HeightRequest = 44, Padding = 0, HorizontalOptions = LayoutOptions.Start };
        edit.Clicked += async (_, _) => await EditDraftAsync();
        var confirm = ShinyUi.Round("✓", 40, filled: true);
        confirm.HorizontalOptions = LayoutOptions.End;
        confirm.Clicked += async (_, _) => await CatchAsync();
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        row.Add(edit);
        Grid.SetColumn(confirm, 1);
        row.Add(confirm);
        _panel.Add(row);
    }

    private async Task EditDraftAsync()
    {
        var choice = await _page.ChooseAsync("Détails du chromatique", "Surnom", "Poké Ball", "Description");
        switch (choice)
        {
            case "Surnom":
                var name = await _page.AskAsync("Surnom", "Comment s'appelle ce chromatique ?", _draftName, false);
                if (!string.IsNullOrWhiteSpace(name)) _draftName = name.Trim();
                break;
            case "Poké Ball":
                var ball = await BallPicker.ChooseAsync(_page, _draftBall);
                if (ball is { } id) _draftBall = id;
                break;
            case "Description":
                var text = await _page.AskAsync("Description", "Un petit mot sur cette capture.", _draftDescription, false);
                if (text is not null) _draftDescription = text.Trim();
                break;
        }
    }

    private async Task CatchAsync()
    {
        _page.Store.Catch(_hunt, string.IsNullOrWhiteSpace(_draftName) ? _hunt.Name : _draftName, _draftBall, _draftDescription);
        await _page.NoticeAsync("Félicitations !", $"{_draftName} est dans ta collection de chromatiques.");
        _changed();
    }

    private void ShowSettings()
    {
        OpenPanel("RÉGLAGES DE LA CHASSE");
        var game = Game;
        var method = Method;
        _panel.Add(Row("Jeu", game?.Name ?? "?"));
        _panel.Add(Row("Méthode", method?.Name ?? "?"));
        if (game is { HasCharm: true }) _panel.Add(Row("Charme Chroma", _hunt.Charm ? "Oui" : "Non"));
        foreach (var line in ShinyHuntText.OptionLines(method, _hunt.Choices))
            _panel.Add(Row(line.Label, line.Value));

        var increment = ShinyUi.Wide("Ajuster l'incrément");
        increment.Clicked += async (_, _) => await AdjustIncrementAsync();
        var timer = ShinyUi.Wide("Ajuster le minuteur");
        timer.Clicked += async (_, _) => await AdjustTimerAsync();
        var encounters = ShinyUi.Wide("Ajuster les rencontres");
        encounters.Clicked += async (_, _) => await AdjustEncountersAsync();
        _panel.Add(increment);
        _panel.Add(timer);
        _panel.Add(encounters);
        _panel.Add(new BoxView { HeightRequest = 2, Color = Color.FromArgb("#3a3b40"), Margin = new Thickness(0, 6) });

        var change = ShinyUi.Round("⟳", 40, filled: true);
        change.Clicked += (_, _) => _page.ShowCreate(_hunt);
        var delete = new Button
        {
            Text = "🗑",
            FontSize = 22,
            TextColor = ShinyUi.AccentText,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 48,
            HeightRequest = 44,
            Padding = 0,
            HorizontalOptions = LayoutOptions.End,
        };
        delete.Clicked += async (_, _) => await DeleteAsync();
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        change.HorizontalOptions = LayoutOptions.Start;
        row.Add(change);
        Grid.SetColumn(delete, 1);
        row.Add(delete);
        _panel.Add(row);
    }

    private async Task AdjustIncrementAsync()
    {
        var text = await _page.AskAsync("Incrément", "Nombre ajouté à chaque rencontre.", _hunt.Increment.ToString(), true);
        if (!int.TryParse(text, out var value) || value is < 1 or > 9999) return;
        _hunt.Increment = value;
        _page.Store.Save();
    }

    private async Task AdjustTimerAsync()
    {
        var text = await _page.AskAsync("Minuteur", "Temps total de la chasse (h:mm:ss).", ShinyUi.Clock(_hunt.TotalNowMs), false);
        if (ShinyUi.ParseDuration(text) is not { } milliseconds) return;
        _hunt.Settle();
        _hunt.TotalMs = milliseconds;
        _page.Store.Save();
        Refresh();
    }

    private async Task AdjustEncountersAsync()
    {
        var text = await _page.AskAsync("Rencontres", "Nombre de rencontres.", _hunt.Encounters.ToString(), true);
        if (!int.TryParse(text, out var value) || value < 0) return;
        _hunt.Encounters = value;
        _page.Store.Save();
        Refresh();
    }

    private async Task DeleteAsync()
    {
        if (!await _page.ConfirmAsync("Supprimer la chasse ?", $"La chasse de {_hunt.Name} sera effacée, sans passer par la collection.", "Supprimer")) return;
        _page.Store.Remove(_hunt);
        _changed();
    }
}

/// <summary>Text lines about the settings of a hunt, shared by the counters and the collection.</summary>
internal static class ShinyHuntText
{
    public static IEnumerable<(string Label, string Value)> OptionLines(HuntMethod? method, IReadOnlyDictionary<string, int> choices)
    {
        if (method is null) yield break;
        foreach (var group in ShinyOdds.OptionsOf(method))
        {
            var index = Math.Clamp(choices.GetValueOrDefault(group.Id), 0, group.Choices.Length - 1);
            yield return (group.Label, group.Choices[index].Label);
        }
    }
}
