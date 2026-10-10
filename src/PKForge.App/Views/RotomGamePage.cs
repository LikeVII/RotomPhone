using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Which game of the profile a page is about (region "alola", game "ultrasun").</summary>
public sealed record ProfileGameRef(string Region, string Game);

/// <summary>
/// The menu of one game: every imported save of it, the prime save first, each with its own small profile.
/// The player imports a save from here; importing the same trainer again updates that save.
/// A temporary layout until this screen is designed.
/// </summary>
public sealed class RotomGamePage : ContentPage
{
    private static readonly Color Teal = Color.FromArgb("#006e72");
    private static readonly Color Navy = Color.FromArgb("#243642");
    private static readonly Color Pill = Color.FromArgb("#eef7ff");
    private static readonly Color Beige = Color.FromArgb("#e3d2c3");
    private static readonly Color Red = Color.FromArgb("#ee2b25");

    private readonly SaveLibrary _library;
    private readonly ISaveEngine _engine;
    private readonly IDocumentPicker _picker;
    private readonly ISaveFileAccess _files;
    private readonly ProfileGameRef _game;
    private readonly ProfileRegion _region;
    private readonly ProfileGame _profileGame;
    private readonly VerticalStackLayout _list = new() { Spacing = 14 };
    private bool _busy;

    public RotomGamePage(ProfileGameRef game, SaveLibrary library, ISaveEngine engine, IDocumentPicker picker, ISaveFileAccess files)
    {
        _game = game;
        _library = library;
        _engine = engine;
        _picker = picker;
        _files = files;
        _region = ProfileCatalog.Regions.First(r => r.Id == game.Region);
        _profileGame = _region.Games.First(g => g.Id == game.Game);
        BackgroundColor = Teal;
        NavigationPage.SetHasNavigationBar(this, false);

        var import = new Button
        {
            Text = "Importer une sauvegarde",
            FontFamily = "RotomUI",
            FontSize = 20,
            TextColor = Colors.White,
            BackgroundColor = Red,
            CornerRadius = 26,
            HeightRequest = 56,
        };
        import.Clicked += async (_, _) => await ImportAsync();

        var icon = new Image
        {
            Source = ImageSource.FromStream(async _ => (Stream?)await FileSystem.OpenAppPackageFileAsync(ProfileCatalog.GameAsset(game.Region, game.Game))),
            HeightRequest = 110,
            WidthRequest = 110,
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Center,
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20, 48, 20, 36),
                Spacing = 16,
                Children =
                {
                    icon,
                    Text(GameName, 30, Colors.White, TextAlignment.Center),
                    Text("Sauvegardes de ce jeu", 16, Beige, TextAlignment.Center),
                    import,
                    _list,
                },
            },
        };
    }

    private string GameName => RotomOrigin.Game(_profileGame.Versions[0]);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Rebuild();
    }

    private static Label Text(string text, double size, Color color, TextAlignment align = TextAlignment.Start, bool bold = false) => new()
    {
        Text = text,
        FontFamily = "RotomUI",
        FontSize = size,
        TextColor = color,
        HorizontalTextAlignment = align,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
    };

    private void Rebuild()
    {
        _list.Children.Clear();
        var saves = _library.ForGame(_game.Region, _game.Game);
        if (saves.Count == 0)
        {
            _list.Children.Add(Text("Aucune sauvegarde pour ce jeu pour le moment.", 16, Pill, TextAlignment.Center));
            return;
        }
        foreach (var save in saves) _list.Children.Add(Card(save));
    }

    /// <summary>The small profile of one save.</summary>
    private View Card(SaveEntry save)
    {
        var regionCaught = save.Caught.Count(species => ProfileCatalog.InDex(_game.Region, species));
        var lines = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                Text(save.Trainer, 24, Colors.White, bold: true),
                Text($"ID : {save.TID:00000}", 16, Pill),
                Text($"Temps de jeu : {save.PlayTime}", 16, Pill),
                Text($"{_region.Title} : {regionCaught}/{ProfileCatalog.Total(_game.Region)}", 16, Pill),
                Text($"Pokémon capturés : {save.Caught.Length}  ·  vus : {save.Seen.Length}", 16, Pill),
                Text("Badges : bientôt", 16, Beige),
                Text($"Mise à jour le {save.UpdatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}", 13, Beige),
            },
        };
        var stack = new VerticalStackLayout { Spacing = 8 };
        if (save.IsPrime)
            stack.Add(new Border
            {
                BackgroundColor = Red,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(12, 4),
                HorizontalOptions = LayoutOptions.Start,
                Content = Text("SAUVEGARDE PRINCIPALE", 12, Colors.White, bold: true),
            });
        stack.Add(lines);
        var card = new Border
        {
            BackgroundColor = Navy,
            Stroke = save.IsPrime ? Beige : Colors.Transparent,
            StrokeThickness = save.IsPrime ? 3 : 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 26 },
            Padding = new Thickness(20),
            Content = stack,
        };
        card.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenActionsAsync(save)) });
        return card;
    }

    private async Task OpenActionsAsync(SaveEntry save)
    {
        var prime = "Définir comme sauvegarde principale";
        var delete = "Supprimer cette sauvegarde";
        var choice = save.IsPrime
            ? await DisplayActionSheetAsync(save.Trainer, "Annuler", delete)
            : await DisplayActionSheetAsync(save.Trainer, "Annuler", delete, prime);
        if (choice == prime)
        {
            _library.SetPrime(save.Id);
            Rebuild();
        }
        else if (choice == delete)
        {
            var confirmed = await DisplayAlertAsync("Supprimer ?",
                $"La copie de la sauvegarde de {save.Trainer} sera supprimée de Rotom Phone. Le fichier d'origine n'est pas touché.", "Supprimer", "Annuler");
            if (!confirmed) return;
            _library.Delete(save.Id);
            Rebuild();
        }
    }

    private async Task ImportAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var document = await _picker.PickSaveAsync();
            if (document is null) return;
            var bytes = await _files.ReadAsync(document.DocumentId);

            // The file says which game it comes from; ask before filing it under another one.
            var described = _engine.TryDescribe(bytes, document.DisplayName);
            if (described is not null)
            {
                var guess = SaveGames.Resolve(described.GameName);
                if (guess.Count > 0 && !guess.Contains((_game.Region, _game.Game)))
                {
                    var go = await DisplayAlertAsync("Un autre jeu ?",
                        $"Cette sauvegarde ressemble à « {described.GameName} ». L'importer dans {GameName} quand même ?", "Importer", "Annuler");
                    if (!go) return;
                }
            }

            var hint = described?.GameName;
            var result = await Task.Run(() => _library.Import(bytes, document.DisplayName, hint, SaveFormat.Auto, _game.Region, _game.Game, GameName));
            Rebuild();
            var message = result.Kind == ImportKind.Updated
                ? $"La sauvegarde de {result.Entry.Trainer} a été mise à jour."
                : result.Entry.IsPrime
                    ? $"La sauvegarde de {result.Entry.Trainer} est ajoutée et devient la sauvegarde principale de {GameName}."
                    : $"La sauvegarde de {result.Entry.Trainer} est ajoutée. La sauvegarde principale de {GameName} ne change pas.";
            await DisplayAlertAsync("Importation terminée", message, "OK");
        }
        catch (Exception error)
        {
            AppLog.Warn("saves", $"Import failed: {error.Message}");
            await DisplayAlertAsync("Importation impossible", "Cette sauvegarde n'a pas pu être lue.", "OK");
        }
        finally
        {
            _busy = false;
        }
    }
}
