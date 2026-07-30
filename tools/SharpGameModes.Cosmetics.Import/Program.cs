using System.Globalization;
using SharpGameModes.Cosmetics.Storage;

var options = ParseArguments(args);
if (!options.TryGetValue("output", out var output))
{
    PrintUsage();
    return 2;
}

try
{
    var repository = new CosmeticsRepository(output);
    repository.EnsureCreated();

    var skinCount = 0;
    var knifeCount = 0;
    if (options.TryGetValue("skins-tsv", out var skinsTsv))
    {
        skinCount = repository.ImportWeaponSkins(ReadSkins(skinsTsv));
    }

    if (options.TryGetValue("knives-tsv", out var knivesTsv))
    {
        knifeCount = repository.ImportKnives(ReadKnives(knivesTsv));
    }

    var snapshot = repository.LoadAll();
    Console.WriteLine($"Database: {repository.DatabasePath}");
    Console.WriteLine($"Imported weapon skin rows: {skinCount}");
    Console.WriteLine($"Imported knife rows: {knifeCount}");
    Console.WriteLine(
        $"Stored totals: skins={snapshot.WeaponSkins.Count}, knives={snapshot.Knives.Count}");
    return 0;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or ArgumentException)
{
    Console.Error.WriteLine($"Import failed: {exception.Message}");
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            PrintUsage();
            Environment.Exit(2);
        }

        values[arguments[index][2..]] = arguments[index + 1];
    }

    return values;
}

static IEnumerable<WeaponSkinPreference> ReadSkins(string path)
{
    using var reader = File.OpenText(path);
    _ = reader.ReadLine() ?? throw new InvalidDataException($"Skin TSV '{path}' is empty.");
    var lineNumber = 1;
    while (reader.ReadLine() is { } line)
    {
        lineNumber++;
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var columns = line.Split('\t');
        if (columns.Length != 15)
        {
            throw new InvalidDataException($"Skin TSV line {lineNumber} has {columns.Length} columns; expected 15.");
        }

        yield return new WeaponSkinPreference(
            ParseUlong(columns[0], lineNumber, "steamid"),
            ParseInt(columns[1], lineNumber, "weapon_team"),
            ParseInt(columns[2], lineNumber, "weapon_defindex"),
            ParseInt(columns[3], lineNumber, "weapon_paint_id"),
            ParseDouble(columns[4], lineNumber, "weapon_wear"),
            ParseInt(columns[5], lineNumber, "weapon_seed"),
            columns[6],
            ParseInt(columns[7], lineNumber, "weapon_stattrak") != 0,
            ParseInt(columns[8], lineNumber, "weapon_stattrak_count"),
            columns[9],
            columns[10],
            columns[11],
            columns[12],
            columns[13],
            columns[14]);
    }
}

static IEnumerable<KnifePreference> ReadKnives(string path)
{
    using var reader = File.OpenText(path);
    _ = reader.ReadLine() ?? throw new InvalidDataException($"Knife TSV '{path}' is empty.");
    var lineNumber = 1;
    while (reader.ReadLine() is { } line)
    {
        lineNumber++;
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var columns = line.Split('\t');
        if (columns.Length != 3)
        {
            throw new InvalidDataException($"Knife TSV line {lineNumber} has {columns.Length} columns; expected 3.");
        }

        yield return new KnifePreference(
            ParseUlong(columns[0], lineNumber, "steamid"),
            ParseInt(columns[1], lineNumber, "weapon_team"),
            columns[2]);
    }
}

static ulong ParseUlong(string value, int line, string column)
    => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed != 0
        ? parsed
        : throw new InvalidDataException($"Line {line} has invalid {column} '{value}'.");

static int ParseInt(string value, int line, string column)
    => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : throw new InvalidDataException($"Line {line} has invalid {column} '{value}'.");

static double ParseDouble(string value, int line, string column)
    => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : throw new InvalidDataException($"Line {line} has invalid {column} '{value}'.");

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: SharpGameModes.Cosmetics.Import --output <cosmetics.db> " +
        "[--skins-tsv <weapon-skins.tsv>] [--knives-tsv <knives.tsv>]");
}
