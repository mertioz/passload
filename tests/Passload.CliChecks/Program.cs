using System.Formats.Tar;
using System.IO.Compression;
using Passload.Cli;

if (OperatingSystem.IsWindows()) return;

var folder = Directory.CreateTempSubdirectory("passload-update-checks-");
try
{
    var executable = Path.Combine(folder.FullName, "passload");
    var archive = Path.Combine(folder.FullName, "update.tar.gz");
    const UnixFileMode permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    File.WriteAllText(executable, "version installée");
    File.SetUnixFileMode(executable, permissions);
    var original = PortableUpdates.FileStamp.Read(executable);

    BuildArchive(archive, new PaxTarEntry(TarEntryType.SymbolicLink, "passload") { LinkName = "../source" });
    try
    {
        await PortableUpdates.ReplaceAsync(executable, original, archive, default);
        throw new Exception("L’archive invalide aurait dû être refusée.");
    }
    catch (InvalidDataException) when (File.ReadAllText(executable) == "version installée") { }

    BuildArchive(archive, new PaxTarEntry(TarEntryType.RegularFile, "passload") { DataStream = new MemoryStream("mise à jour de test"u8.ToArray()) });
    if (!await PortableUpdates.ReplaceAsync(executable, original, archive, default) ||
        File.ReadAllText(executable) != "mise à jour de test" || File.GetUnixFileMode(executable) != permissions)
        throw new Exception("Le remplacement doit conserver le mode exécutable.");
    if (await PortableUpdates.ReplaceAsync(executable, original, archive, default))
        throw new Exception("Une autre mise à jour a déjà remplacé l’exécutable initial.");
    Console.WriteLine("Archive invalide refusée, remplacement vérifié, permissions préservées, mise à jour concurrente détectée.");
}
finally { folder.Delete(recursive: true); }

static void BuildArchive(string destination, TarEntry entry)
{
    using var file = File.Create(destination);
    using var gzip = new GZipStream(file, CompressionLevel.Fastest);
    using var tar = new TarWriter(gzip);
    tar.WriteEntry(entry);
}
