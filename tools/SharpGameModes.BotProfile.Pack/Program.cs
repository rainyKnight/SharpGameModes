using System.Security.Cryptography;
using SteamDatabase.ValvePak;

if (args is ["--extract", var extractVpkPath, var outputPath])
{
    using var package = new Package();
    package.Read(Path.GetFullPath(extractVpkPath));
    package.VerifyHashes();

    var entry = package.FindEntry("botprofile.db")
        ?? throw new InvalidDataException(
            $"{extractVpkPath} does not contain botprofile.db.");
    package.ReadEntry(entry, out var database);
    await File.WriteAllBytesAsync(Path.GetFullPath(outputPath), database);
    Console.WriteLine(
        $"{entry.GetFullPath()}: {database.Length} bytes, " +
        $"sha256 {Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant()}.");
    return 0;
}

if (args.Length != 1)
{
    Console.Error.WriteLine(
        "Usage: SharpGameModes.BotProfile.Pack <config/csgo/overrides>\n" +
        "       SharpGameModes.BotProfile.Pack --extract <botprofile.vpk> <output.db>");
    return 2;
}

var overridesRoot = Path.GetFullPath(args[0]);
foreach (var tier in new[] { "Low", "Medium", "HLTVTop10", "High" })
{
    var tierDirectory = Path.Combine(overridesRoot, tier);
    var databasePath = Path.Combine(tierDirectory, "botprofile.db");
    var vpkPath = Path.Combine(tierDirectory, "botprofile.vpk");
    var temporaryPath = vpkPath + ".tmp";

    if (!File.Exists(databasePath))
    {
        Console.Error.WriteLine($"Missing input: {databasePath}");
        return 3;
    }

    var database = await File.ReadAllBytesAsync(databasePath);
    try
    {
        using (var package = new Package())
        {
            package.AddFile("botprofile.db", database);
            package.Write(temporaryPath);
        }

        using (var verification = new Package())
        {
            verification.Read(temporaryPath);
            verification.VerifyHashes();

            var entry = verification.FindEntry("botprofile.db")
                ?? throw new InvalidDataException(
                    $"{temporaryPath} does not contain botprofile.db.");
            verification.ReadEntry(entry, out var packedDatabase);
            if (!database.AsSpan().SequenceEqual(packedDatabase))
            {
                throw new InvalidDataException(
                    $"{temporaryPath} does not reproduce {databasePath}.");
            }
        }

        File.Move(temporaryPath, vpkPath, overwrite: true);
    }
    finally
    {
        File.Delete(temporaryPath);
    }

    Console.WriteLine(
        $"{tier}: {database.Length} bytes, " +
        $"sha256 {Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant()}, " +
        $"wrote {new FileInfo(vpkPath).Length} byte VPK.");
}

return 0;
