using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Passload.Core;

namespace Passload.Checks;

internal static class DownloadHistoryCheck
{
    public static async Task RunAsync(string directory, string file, string empty, Action<bool, string> check, bool useNetwork)
    {
        var path = Path.Combine(directory, "download-check.jsonl");
        var history = new DownloadHistory(path);
        var catalog = new ShareCatalog(history);
        var network = NetworkInfo.Read();
        await using var server = await TransferServer.StartAsync(catalog, 0, network.Addresses, loopbackOnly: !useNetwork);
        var source = useNetwork ? network.LanAddress ?? throw new InvalidOperationException("Interface réseau indisponible.") : IPAddress.Loopback;
        var origin = NetworkInfo.Origin(source, server.Port);
        var files = SharedFile.Select([file, empty]);
        var share = catalog.Add(files, TimeSpan.FromMinutes(1), ShareScope.Local, origin, server.Fingerprint, "Vérification");
        var endpoint = share.Link.GetLeftPart(UriPartial.Path);
        try
        {
            using var browser = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    cert?.GetCertHashString(HashAlgorithmName.SHA256) == server.Fingerprint
            });
            using var head = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Head, endpoint + "/files/0"));
            await browser.GetStringAsync(endpoint);
            await browser.GetStringAsync(endpoint + "/manifest");
            check(history.Recent().Length == 0, "pages, métadonnées et HEAD absents de l’historique");

            async Task Range(long from, long to)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/files/0");
                request.Headers.Range = new RangeHeaderValue(from, to);
                request.Headers.Add("X-Forwarded-For", "203.0.113.22");
                using var response = await browser.SendAsync(request);
                response.EnsureSuccessStatusCode();
                await response.Content.CopyToAsync(Stream.Null);
            }

            if (useNetwork)
            {
                await Range(0, 3);
                await Range(0, 3);
                check(history.Recent().Length == 0, "une plage partielle répétée ne compte pas");
                await Range(4, files[0].Size - 1);
                await WaitForRecords(history, 1);
                check(history.Recent() is [{ Address: { } observed }] && observed == source.ToString(),
                    "reprise complète comptée une fois et IP observée sans en-tête transmis");
                await browser.GetByteArrayAsync(endpoint + "/files/1");
                await browser.GetByteArrayAsync(endpoint + "/all.zip");
                await WaitForRecords(history, 3);
                check(history.Recent().Length == 3 && history.Recent().Any(item => item.FileName == "Passload.zip"),
                    "fichier vide et ZIP comptés chacun une fois");
            }
            else
            {
                await browser.GetByteArrayAsync(endpoint + "/files/0");
                check(history.Recent().Length == 0, "aperçu sur la boucle locale exclu");
            }

            share.RecordWebRtcDownload(0, "::ffff:192.0.2.55", false);
            share.RecordWebRtcDownload(-1, null, true);
            share.RecordWebRtcDownload(-5, "192.0.2.56", false);
            share.UpdateDetails("Archive de démonstration", "Texte lu par le destinataire.");
            catalog.Revoke(share.Token);
            var recovered = new DownloadHistory(path).Recent();
            check(recovered.Length == (useNetwork ? 5 : 2) && recovered.All(item => item.ShareId == share.HistoryId),
                "historique du lien conservé après révocation et redémarrage");
            check(recovered.All(item => item.ShareName == "Archive de démonstration"), "nouveau titre appliqué aux entrées du même partage");
            check(recovered[0] is { Transport: DownloadTransport.EncryptedRelay, Address: null } &&
                recovered[1] is { Transport: DownloadTransport.WebRtc, Address: "192.0.2.55" },
                "IP WebRTC normalisée et IP inconnue sous relais");
            var contents = await File.ReadAllTextAsync(path);
            check(!contents.Contains(share.Token) && !contents.Contains(directory, StringComparison.OrdinalIgnoreCase),
                "journal local sans jeton de lien ni chemin des fichiers");
            var parallel = new DownloadHistory(Path.Combine(directory, "download-new.jsonl"), path);
            check(parallel.Recent().Length == recovered.Length && await File.ReadAllTextAsync(path) == contents,
                "historique de la session précédente lu sans modifier son journal");
        }
        finally { catalog.EndAll(); }
    }

    private static async Task WaitForRecords(DownloadHistory history, int expected)
    {
        for (var attempt = 0; attempt < 20 && history.Recent().Length < expected; attempt++)
            await Task.Delay(25);
    }
}
