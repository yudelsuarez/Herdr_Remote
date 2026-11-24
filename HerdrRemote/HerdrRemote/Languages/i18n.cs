using System.Reflection;

namespace Languages;

[AcceptEmptyServiceProvider]
[ContentProperty("Key")]
public class i18n : IMarkupExtension
{
    public string Key { get; set; } = string.Empty;
    public static string defaultLanguageCode = "en-US";
    static string languagesfolder = "Languages" + ".";
    public static Dictionary<string, LanguagesData> AvilableLanguage = new Dictionary<string, LanguagesData> { { "en-US", new LanguagesData { Name = "English" } } };

    static Dictionary<string, Dictionary<string, string>> languages = new Dictionary<string, Dictionary<string, string>>();

    // The keys Translate already reported missing in this run.
    static readonly HashSet<string> reportedMissing = new(StringComparer.OrdinalIgnoreCase);

    public static void setLanguage(string language)
    {
        AvilableLanguage = GetAvilableLanguage();

        if (defaultLanguageCode == language)
        {
            return;
        }

        // The new code is published only after its catalog (below): a Translate on another thread never sees a code without its catalog.
        string code = defaultLanguageCode;
        if (AvilableLanguage.ContainsKey(language))
        {
            code = language;
        }
        else
        {
            // No exact match: fall back to another region of the same language.

            bool foundSameContry = false;
            string[] languageContryAndRegion = language.Split("-");

            if (languageContryAndRegion.Length == 2)
            {
                foreach (string Available in AvilableLanguage.Keys)
                {
                    if (Available.Contains(languageContryAndRegion[0]))
                    {// same country OK!
                        code = Available;
                        foundSameContry = true;
                        break;
                    }
                }
            }

            if (foundSameContry == false)
            {
                var ex = new Exception("i18n.ProvideValue", new Exception($"Language NOT SUPPORTED [{language}], stay in current Default [{defaultLanguageCode}]"));
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}\r\nDetails: {ex.InnerException?.Message}");
                return;
            }
        }

        // Keys compare ignoring case: one lookup per Translate, no lowered copy of the key per call.
        // Built whole and then published in one assignment: Translate runs on other threads too and must never read a catalog being written.
        Dictionary<string, string> catalog = new(StringComparer.OrdinalIgnoreCase);
        string[] lines = LoadTextLines(AvilableLanguage[code].ResourcePath);

        if (lines.Length > 0)
        {
            foreach (string line in lines)
            {
                // A line is "English key=translation", split at the FIRST '=': the English key must never contain '='.
                int eq = line.IndexOf('=');
                if ((line.Length > 0) && (line[0] != '#' && line[0] != ';' && line[0] != '/') && eq > -1)
                {
                    string key = line[..eq].Trim().ToLowerInvariant();
                    string text = line[(eq + 1)..].Trim();
                    catalog[key] = text;
                }
            }
        }

        // The previous catalog stays in the new dictionary, so a reader still holding the previous code keeps finding it.
        languages = new Dictionary<string, Dictionary<string, string>>(languages) { [code] = catalog };
        defaultLanguageCode = code;
    }

    static Dictionary<string, LanguagesData> GetAvilableLanguage()
    {
        var foundLenguages = new Dictionary<string, LanguagesData> { { "en-US", new LanguagesData { Name = "English" } } };

        var ManifestResourceNames = typeof(i18n).GetTypeInfo().Assembly.GetManifestResourceNames();

        foreach (var res in ManifestResourceNames)
        {
            var z = res.IndexOf(languagesfolder, 0, StringComparison.InvariantCultureIgnoreCase);
            if (z > -1)
            {
                if (res.Contains('_'))
                {
                    var start = z + languagesfolder.Length;
                    var end = res.IndexOf('.', start);

                    var codename = res.Substring(start, end - start);
                    var code_name = codename.Split('_');

                    foundLenguages.Add(code_name[0], new LanguagesData { Name = code_name[1], ResourcePath = res });
                }
            }
        }

        return foundLenguages;
    }

    static string[] LoadTextLines(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return []; }
        List<string> linesreaded = new List<string>();

        try
        {
            Stream? stream = typeof(i18n).GetTypeInfo().Assembly.GetManifestResourceStream(filePath);

            if (stream != null)
            {
                using (var reader = new StreamReader(stream))
                {
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine();
                        if (line != null)
                        {
                            linesreaded.Add(line);
                        }
                    }
                }
            }

        }
        catch (Exception ex) 
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return (linesreaded.Count > 0) ? linesreaded.ToArray() : [];
    }

    public object ProvideValue(IServiceProvider serviceProvider)
    {
        return Translate(Key);
    }

    public static string Translate(String Key)
    {
        if (string.IsNullOrEmpty(Key))
        {
            return string.Empty;
        }
        if (defaultLanguageCode != "en-US")
        {
            if (languages.TryGetValue(defaultLanguageCode, out Dictionary<string, string>? catalog))
            {
                bool removed = false;
                if (Key[Key.Length - 1] == ':')
                {
                    Key = Key.Substring(0, Key.Length - 1);
                    removed = true;
                }

                if (catalog.TryGetValue(Key, out string? converted))
                {
                    if (removed) { converted += ':'; }
                    return converted;
                }
                else
                {
                    // One line per missing key per run, no exception objects. Translate runs on other threads too.
                    lock (reportedMissing)
                    {
                        if (reportedMissing.Add(Key)) { System.Diagnostics.Debug.WriteLine($"i18n: key not found [{Key}]"); }
                    }
                }
            }
            else
            {
                var ex = new Exception("i18n.ProvideValue", new Exception($"languages code Not found [{defaultLanguageCode}]"));
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}\r\nDetails: {ex.InnerException?.Message}");
            }
        }

        return Key;
    }

    // One English word the Spanish web says differently per entity (Active: credit note "Activa", product "Activo"). The catalog line is
    // "context|Key=Spanish"; without it (and in English) the plain key applies.
    public static string Translate(string context, string Key)
    {
        if (defaultLanguageCode != "en-US"
            && languages.TryGetValue(defaultLanguageCode, out Dictionary<string, string>? catalog)
            && catalog.TryGetValue($"{context}|{Key}", out string? converted))
        {
            return converted;
        }

        return Translate(Key);
    }

}

public class LanguagesData
{
    public string Name = string.Empty;
    public string ResourcePath = string.Empty;
}