using System.Reflection;

namespace Composa.Filters;

/// <summary>
/// The film looks bundled with the Color Lookup adjustment: four 33-point tables from sguyader/FilmSim (CC0),
/// embedded from <c>Looks/*.cube</c> and parsed the first time each is asked for. They are named for what they
/// do, never for the film stocks they were measured from, whose names are trademarks; the provenance is in
/// <c>packaging/THIRD-PARTY-NOTICES.txt</c> and each file's own comment.
/// </summary>
public static class Looks
{
    private static readonly (string Name, string File, string Description)[] Table =
    [
        ("Fine Mono", "fine-mono", "Black and white with smooth, finely graded tones."),
        ("Muted Chrome", "muted-chrome", "Softened color with restrained highlights and a cool cast."),
        ("Standard Slide", "standard-slide", "Natural, balanced color with a little extra contrast."),
        ("Vivid Slide", "vivid-slide", "Saturated color and strong contrast, as for landscapes.")
    ];

    private static readonly Dictionary<string, Lazy<ColorLattice>> Loaded =
        Table.ToDictionary(look => look.Name, look => new Lazy<ColorLattice>(() => Read(look.File)));

    /// <summary>The looks' names, in the order the dialog shows them.</summary>
    public static IReadOnlyList<string> Names { get; } = Table.Select(look => look.Name).ToArray();

    /// <summary>One line on what a look does, for its tooltip.</summary>
    public static string Describe(string name) => Table.FirstOrDefault(look => look.Name == name).Description ?? "";

    /// <summary>The look's lattice, or null when no bundled look has that name.</summary>
    public static ColorLattice? Find(string name) => Loaded.TryGetValue(name, out var lattice) ? lattice.Value : null;

    private static ColorLattice Read(string file)
    {
        using var stream = typeof(Looks).Assembly.GetManifestResourceStream($"Looks/{file}.cube") ?? throw new InvalidOperationException($"The look {file} is not embedded.");
        using var reader = new StreamReader(stream);
        return ColorLattice.ParseCube(reader.ReadToEnd());
    }
}
