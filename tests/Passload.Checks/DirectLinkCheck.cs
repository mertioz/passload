using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Passload.Core;

namespace Passload.Checks;

internal static class DirectLinkCheck
{
    private const string Text = "Passload direct address smoke test.";

    public static async Task RunAsync()
    {
        // The one-time channel carries only the invitation to a synthetic share.
        var workflow = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH")
            ?? throw new InvalidOperationException("Événement GitHub Actions requis.");
        using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(workflow));
        var probe = payload.RootElement.GetProperty("inputs").GetProperty("probe").GetString();
        if (!Guid.TryParseExact(probe, "N", out var channel)) throw new FormatException("Identifiant de test invalide.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        using var rendezvous = new HttpClient { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 4096 };
        var link = (await rendezvous.GetStringAsync($"https://ppng.io/passload-direct-check-{channel:N}", token)).Trim();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var publicLink) ||
            !IPAddress.TryParse(publicLink.IdnHost, out var address) || !NetworkInfo.IsPublic(address))
            throw new InvalidOperationException("Le test attend un lien vers une adresse IP publique.");

        using var receiver = new ReceiveClient(link);
        var manifest = await receiver.InspectAsync(token);
        var text = Encoding.UTF8.GetBytes(Text);
        var bytes = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var expected = new Dictionary<string, byte[]> { ["probe.txt"] = text, ["probe.bin"] = bytes };
        if (manifest.Files.Length != expected.Count || manifest.Files.Any(file => !expected.TryGetValue(file.Name, out var sample) || file.Size != sample.Length))
            throw new InvalidDataException("Sélection de démonstration inattendue.");

        var folder = Directory.CreateTempSubdirectory("passload-direct-check-");
        var individual = Path.Combine(folder.FullName, "received.txt");
        var archive = Path.Combine(folder.FullName, "received.zip");
        try
        {
            var first = manifest.Files.Single(file => file.Name == "probe.txt");
            await receiver.DownloadAsync(first, individual, null, token);
            var received = await File.ReadAllBytesAsync(individual, token);
            if (!text.SequenceEqual(received))
                throw new InvalidDataException("Le premier fichier diffère de l’original.");
            await receiver.DownloadAsync(null, archive, null, token);
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count != expected.Count) throw new InvalidDataException("Archive incomplète.");
            foreach (var (name, sample) in expected)
            {
                using var content = zip.GetEntry(name)?.Open() ?? throw new InvalidDataException("Entrée manquante.");
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, token);
                if (!sample.SequenceEqual(buffer.ToArray())) throw new InvalidDataException("L’archive diffère des fichiers d’origine.");
            }
            Console.WriteLine("HTTPS direct depuis un autre réseau. Certificat vérifié, fichier et ZIP identiques.");
        }
        finally
        {
            File.Delete(archive);
            File.Delete(individual);
            folder.Delete();
        }
    }
}
