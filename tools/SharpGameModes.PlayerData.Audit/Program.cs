using SharpGameModes.PlayerData.Storage;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: SharpGameModes.PlayerData.Audit <autoteamlock_player_data.db>");
    return 2;
}

try
{
    var repository = new PlayerRatingRepository(args[0]);
    var ratings = repository.LoadAll();
    var usable = ratings.Values.Count(rating => rating.HistoryCount > 0 && rating.Rating > 0);
    var minimum = usable > 0
        ? ratings.Values.Where(rating => rating.HistoryCount > 0 && rating.Rating > 0).Min(rating => rating.Rating)
        : 0;
    var maximum = usable > 0
        ? ratings.Values.Where(rating => rating.HistoryCount > 0 && rating.Rating > 0).Max(rating => rating.Rating)
        : 0;

    Console.WriteLine($"Database: {repository.DatabasePath}");
    Console.WriteLine($"Records: {ratings.Count}");
    Console.WriteLine($"Usable ratings: {usable}");
    Console.WriteLine($"Rating range: {minimum:F4}..{maximum:F4}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Audit failed: {exception.Message}");
    return 1;
}
