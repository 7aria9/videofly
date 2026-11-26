using System.Windows;

namespace VideoFly;

/// <summary>Une disposition de vignettes qui remplit toute la zone disponible.</summary>
public sealed record TileLayout(string Id, string Name, IReadOnlyList<Rect> Rects);

/// <summary>
/// Calcule les dispositions possibles pour un nombre de vignettes donné.
/// Exemple pour 3 : « 1 à gauche, 2 à droite », « 1 en haut, 2 en bas », « 3 côte à côte », « 3 l'une sous l'autre ».
/// </summary>
public static class TileLayouts
{
    public const string Columns = "columns";
    public const string Rows = "rows";
    public const string Grid = "grid";
    public const string GridByColumns = "grid-columns";
    public const string MainLeft = "main-left";
    public const string MainTop = "main-top";

    /// <summary>Toutes les dispositions différentes pour <paramref name="count"/> vignettes dans <paramref name="area"/>.</summary>
    public static List<TileLayout> For(int count, Rect area)
    {
        var result = new List<TileLayout>();
        if (count <= 0 || area.Width <= 0 || area.Height <= 0) return result;

        void Add(string id, string name, List<Rect> rects)
        {
            // On n'ajoute pas deux fois la même disposition (ex. pour 2 vignettes, « 1 à gauche, 1 à droite » = « 2 côte à côte »).
            if (result.Any(l => SameRects(l.Rects, rects))) return;
            result.Add(new TileLayout(id, name, rects));
        }

        if (count == 1)
        {
            Add(Grid, "Plein écran", new List<Rect> { area });
            return result;
        }

        Add(Columns, $"{count} côte à côte", SplitColumns(area, count));
        Add(Rows, $"{count} l'une sous l'autre", SplitRows(area, count));
        if (count >= 3)
        {
            Add(MainLeft, $"1 à gauche, {count - 1} à droite", MainLeftLayout(area, count));
            Add(MainTop, $"1 en haut, {count - 1} en bas", MainTopLayout(area, count));
        }
        if (count >= 3)
        {
            Add(Grid, count == 3 ? "2 en haut, 1 en bas" : "Grille", GridLayout(area, count, byColumns: false));
            Add(GridByColumns, count == 3 ? "2 à gauche, 1 à droite" : "Grille (par colonnes)", GridLayout(area, count, byColumns: true));
        }
        return result;
    }

    /// <summary>La disposition <paramref name="id"/> pour ce nombre de vignettes, ou la plus proche si elle n'existe pas.</summary>
    public static TileLayout? Find(string? id, int count, Rect area)
    {
        var layouts = For(count, area);
        return layouts.FirstOrDefault(l => l.Id == id)
               // Ex. « Grille » choisie avec 4 vignettes, puis il n'en reste que 2 : on prend la première proposée.
               ?? layouts.FirstOrDefault();
    }

    // ---- Briques de base --------------------------------------------------------------

    private static List<Rect> SplitColumns(Rect area, int n)
    {
        var rects = new List<Rect>(n);
        for (int i = 0; i < n; i++)
        {
            double x0 = Math.Round(area.X + area.Width * i / n), x1 = Math.Round(area.X + area.Width * (i + 1) / n);
            rects.Add(new Rect(x0, area.Y, x1 - x0, area.Height));
        }
        return rects;
    }

    private static List<Rect> SplitRows(Rect area, int n)
    {
        var rects = new List<Rect>(n);
        for (int i = 0; i < n; i++)
        {
            double y0 = Math.Round(area.Y + area.Height * i / n), y1 = Math.Round(area.Y + area.Height * (i + 1) / n);
            rects.Add(new Rect(area.X, y0, area.Width, y1 - y0));
        }
        return rects;
    }

    /// <summary>
    /// Grille qui remplit toute la zone : si la dernière ligne (ou colonne) est incomplète,
    /// ses vignettes s'élargissent (ou s'allongent) pour combler le vide.
    /// </summary>
    private static List<Rect> GridLayout(Rect area, int n, bool byColumns)
    {
        int major = (int)Math.Ceiling(Math.Sqrt(n));               // vignettes par ligne (ou par colonne)
        int lines = (int)Math.Ceiling(n / (double)major);          // nombre de lignes (ou de colonnes)
        var bands = byColumns ? SplitColumns(area, lines) : SplitRows(area, lines);

        var rects = new List<Rect>(n);
        for (int line = 0; line < lines; line++)
        {
            int inLine = Math.Min(major, n - line * major);
            rects.AddRange(byColumns ? SplitRows(bands[line], inLine) : SplitColumns(bands[line], inLine));
        }
        return rects;
    }

    /// <summary>La première vignette occupe la moitié gauche, les autres se partagent la moitié droite.</summary>
    private static List<Rect> MainLeftLayout(Rect area, int n)
    {
        var halves = SplitColumns(area, 2);
        var rects = new List<Rect> { halves[0] };
        rects.AddRange(n - 1 <= 3 ? SplitRows(halves[1], n - 1) : GridLayout(halves[1], n - 1, byColumns: true));
        return rects;
    }

    /// <summary>La première vignette occupe la moitié haute, les autres se partagent la moitié basse.</summary>
    private static List<Rect> MainTopLayout(Rect area, int n)
    {
        var halves = SplitRows(area, 2);
        var rects = new List<Rect> { halves[0] };
        rects.AddRange(n - 1 <= 3 ? SplitColumns(halves[1], n - 1) : GridLayout(halves[1], n - 1, byColumns: false));
        return rects;
    }

    /// <summary>Même découpage de la zone, quel que soit l'ordre des vignettes.</summary>
    private static bool SameRects(IReadOnlyList<Rect> first, IReadOnlyList<Rect> second)
    {
        if (first.Count != second.Count) return false;
        var a = first.OrderBy(r => Math.Round(r.X)).ThenBy(r => Math.Round(r.Y)).ToList();
        var b = second.OrderBy(r => Math.Round(r.X)).ThenBy(r => Math.Round(r.Y)).ToList();
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].X - b[i].X) > 1 || Math.Abs(a[i].Y - b[i].Y) > 1 ||
                Math.Abs(a[i].Width - b[i].Width) > 1 || Math.Abs(a[i].Height - b[i].Height) > 1)
                return false;
        return true;
    }
}
