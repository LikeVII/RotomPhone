using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Placeholder profile screen until its design arrives: the same counts as the home screen's triangle.</summary>
public sealed class RotomProfilePage : ContentPage
{
    public RotomProfilePage(IBankService bank, TrainerProfileStore profiles)
    {
        BackgroundColor = Color.FromArgb("#9ADDDA");
        NavigationPage.SetHasNavigationBar(this, false);
        var all = bank.GetAll();
        var name = profiles.Profiles.FirstOrDefault()?.DisplayName is { Length: > 0 } n ? n : "Dresseur";
        Label Line(string text, double size) => new()
        {
            Text = text,
            FontFamily = "RotomUI",
            FontSize = size,
            TextColor = Color.FromArgb("#4a4b47"),
            HorizontalTextAlignment = TextAlignment.Center,
        };
        Content = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 14,
            Children =
            {
                Line("Profil", 34),
                Line(name, 26),
                Line($"Pokémon : {all.Count}", 20),
                Line($"Chromatiques : {all.Count(e => e.Info.Shiny)}", 20),
                Line($"Espèces : {all.Select(e => e.Info.Species).Distinct().Count()}", 20),
                Line("Cet écran sera dessiné bientôt.", 14),
            },
        };
    }
}
