using System.Text.RegularExpressions;

namespace Etiquetador.Core.Analysis;

public enum Explicitness { Unknown, Clean, Explicit }

/// <summary>Deduce si una pista es "clean" o explícita/"dirty" a partir del nombre y el título.</summary>
public static class ExplicitDetector
{
    private static readonly Regex ExplicitRe = new(
        @"\b(explicit|explicita|explícita|dirty|uncensored|sucia)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CleanRe = new(
        @"\b(clean|censored|censurada|radio\s+edit|limpia)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Explicitness Detect(Track t)
    {
        var hay = (t.FileName + " " + (t.Title ?? "")); // nombre de archivo + título
        if (ExplicitRe.IsMatch(hay)) return Explicitness.Explicit;
        if (CleanRe.IsMatch(hay)) return Explicitness.Clean;
        return Explicitness.Unknown;
    }

    public static string Label(Explicitness e) => e switch
    {
        Explicitness.Explicit => "Explícito",
        Explicitness.Clean => "Clean",
        _ => "",
    };

    public static string Label(Track t) => Label(Detect(t));
}
