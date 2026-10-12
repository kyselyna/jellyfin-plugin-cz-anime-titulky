using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CzAnimeTitulky.Util;

/// <summary>Czech vs Slovak by characteristic letters (hiyori labels are sometimes wrong).</summary>
public static partial class LanguageDetector
{
    /// <returns>"cs" or "sk".</returns>
    public static string Detect(string text)
    {
        var cz = CzechLetters().Count(text);
        var sk = SlovakLetters().Count(text);
        return sk > cz ? "sk" : "cs";
    }

    [GeneratedRegex("[řůěŘŮĚ]")]
    private static partial Regex CzechLetters();

    [GeneratedRegex("[äôľĺŕÄÔĽĹŔ]")]
    private static partial Regex SlovakLetters();
}
