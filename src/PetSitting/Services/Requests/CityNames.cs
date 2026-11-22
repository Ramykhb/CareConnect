using System.Globalization;
namespace PetSitting.Services.Requests;

// Requests only reach sitters whose city matches exactly, so a typo hides a request from everyone.
public static class CityNames
{
    // A city with no sitters that is one or two letters away from a city that has sitters is most likely a typo.
    public static string? Suggest(string? city, IEnumerable<string?> known)
    {
        var typed = city?.Trim() ?? "";
        if (typed.Length < 3) return null;
        var cities = known.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k!.Trim()).ToList();
        if (cities.Any(k => string.Equals(k, typed, StringComparison.OrdinalIgnoreCase))) return null;
        var limit = typed.Length <= 4 ? 1 : 3;   // only a question: the customer can keep what they typed
        return cities.Select(k => (City: k, Distance: Distance(k.ToLowerInvariant(), typed.ToLowerInvariant())))
            .Where(x => x.Distance <= limit).OrderBy(x => x.Distance)
            .Select(x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.City.ToLowerInvariant())).FirstOrDefault();
    }

    public static IReadOnlyList<string> Distinct(IEnumerable<string?> cities) =>
        cities.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c!.Trim().ToLowerInvariant()))
            .Distinct().OrderBy(c => c).ToList();

    // Edit distance where swapping two neighbouring letters counts as one change.
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
