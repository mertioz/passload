using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Mono.Nat;
using Passload.Checks;
using Passload.Core;

var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("Échec : " + label);
    Console.WriteLine("OK   " + label);
    passed++;
}
async Task Throws<T>(Func<Task> operation, string label) where T : Exception
{
    try { await operation(); }
    catch (T) { Check(true, label); return; }
    throw new InvalidOperationException("Échec : " + label);
}

var folder = Directory.CreateTempSubdirectory("passload-check-");
var catalog = new ShareCatalog();
try
{
    var file = Path.Combine(folder.FullName, "été & lumière.txt");
    var twin = Path.Combine(folder.CreateSubdirectory("bis").FullName, Path.GetFileName(file));
    var empty = Path.Combine(folder.FullName, "vide.bin");
    await File.WriteAllTextAsync(file, "première version ☀");
    await File.WriteAllTextAsync(twin, "deuxième");
    await File.WriteAllBytesAsync(empty, []);
    var files = SharedFile.Select([file, file, twin, empty]);
    Check(files.Count == 3, "sélection multiple et dédoublonnage des chemins");
    Check(files[1].ZipName == "été & lumière (2).txt", "homonymes conservés dans l’archive");
    await using var server = await TransferServer.StartAsync(catalog, 0, [], loopbackOnly: true);
    var origin = NetworkInfo.Origin(IPAddress.Loopback, server.Port);
    Share NewShare(TimeSpan duration) => catalog.Add(files, duration, ShareScope.Local, origin, server.Fingerprint, "Test local");
    var share = NewShare(TimeSpan.FromHours(1));
    var other = NewShare(TimeSpan.FromHours(1));
    Check(share.Token != other.Token && share.Token.Length == 43, "liens indépendants à identifiants aléatoires");
    var address = share.Link.GetLeftPart(UriPartial.Path);
    using var http = new HttpClient(new HttpClientHandler
    {
        UseProxy = false, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == server.Fingerprint
    });
    var page = await http.GetStringAsync(address);
    Check(page.Contains("Tout télécharger en ZIP") && WebUtility.HtmlDecode(page).Contains("été & lumière.txt"), "page web et noms accentués");
    Check(!page.Contains(folder.FullName, StringComparison.OrdinalIgnoreCase) && !page.Contains(share.Link.Fragment), "chemins locaux et empreinte absents de la page");
    using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, address + "/files/0"));
    Check(head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == files[0].Size && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "métadonnées HEAD et taille exacte");
    Check(head.Content.Headers.ContentDisposition?.DispositionType == "attachment" && head.Content.Headers.ContentDisposition.FileNameStar == files[0].Name, "pièce jointe avec nom Unicode");
    Check(head.Headers.CacheControl?.NoStore == true && head.Headers.Contains("Referrer-Policy"), "confidentialité des réponses web");
    var original = await File.ReadAllBytesAsync(file);
    Check((await http.GetByteArrayAsync(address + "/files/0")).SequenceEqual(original), "contenu individuel fidèle à l’octet");
    var range = new HttpRequestMessage(HttpMethod.Get, address + "/files/0");
    range.Headers.Range = new RangeHeaderValue(1, 4);
    using var partial = await http.SendAsync(range);
    Check(partial.StatusCode == HttpStatusCode.PartialContent && (await partial.Content.ReadAsByteArrayAsync()).SequenceEqual(original[1..5]), "reprise HTTP sur une plage d’octets");
    Check((await http.GetByteArrayAsync(address + "/files/2")).Length == 0, "téléchargement d’un fichier vide");
    Check((await http.GetAsync(address + "/files/99")).StatusCode == HttpStatusCode.NotFound, "fichier hors sélection indisponible");
    Check((await http.GetAsync(origin)).StatusCode == HttpStatusCode.NotFound, "absence d’annuaire public des partages");
    Check((await http.PostAsync(address, null)).StatusCode == HttpStatusCode.MethodNotAllowed, "partage en lecture seule");
    using var receiver = new ReceiveClient(share.Link.AbsoluteUri);
    var manifest = await receiver.InspectAsync(default);
    Check(manifest.Files.Length == 3 && manifest.Files[0].Name == files[0].Name && manifest.ExpiresAt == share.ExpiresAt, "lecture du manifeste avec empreinte TLS");
    var received = Path.Combine(folder.FullName, "reception.dat");
    await receiver.DownloadAsync(manifest.Files[0], received, null, default);
    Check((await File.ReadAllBytesAsync(received)).SequenceEqual(original), "réception native et écriture sur disque");
    await Throws<IOException>(() => receiver.DownloadAsync(manifest.Files[1], received, null, default), "aucun écrasement sans choix explicite");
    Check((await File.ReadAllBytesAsync(received)).SequenceEqual(original) && !Directory.EnumerateFiles(folder.FullName, ".passload-*.part").Any(), "destination préservée et fichier temporaire nettoyé");
    var archive = Path.Combine(folder.FullName, "group.zip");
    await receiver.DownloadAsync(null, archive, null, default);
    using (var zip = ZipFile.OpenRead(archive))
    {
        Check(zip.Entries.Select(e => e.FullName).SequenceEqual(files.Select(f => f.ZipName)), "ZIP complet avec extensions et homonymes");
        using var reader = new StreamReader(zip.Entries[1].Open());
        Check(await reader.ReadToEndAsync() == "deuxième", "contenu du deuxième document dans le ZIP");
    }
    using var wrong = new ReceiveClient(address + "#sha256=" + new string('0', 64));
    await Throws<HttpRequestException>(() => wrong.InspectAsync(default), "refus d’un certificat qui diffère de l’empreinte du lien");
    await Throws<FormatException>(() => Task.FromResult(new ReceiveClient(address)), "lien incomplet rejeté en réception native");
    Check(ReceiveClient.SuggestedName(@"C:\dossier\CON.txt") == "_CON.txt", "nom de destination Windows valide");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        await Throws<OperationCanceledException>(() => receiver.DownloadAsync(manifest.Files[1], received, null, canceled.Token), "annulation avant téléchargement");
        Check((await File.ReadAllTextAsync(received)) == "première version ☀", "destination existante préservée après annulation");
    }
    var shortShare = NewShare(TimeSpan.FromMilliseconds(150));
    await Task.Delay(250);
    Check((await http.GetAsync(shortShare.Link)).StatusCode == HttpStatusCode.NotFound && !shortShare.Active, "expiration automatique du lien");
    catalog.Revoke(share.Token);
    Check((await http.GetAsync(address)).StatusCode == HttpStatusCode.NotFound && (await http.GetAsync(other.Link)).IsSuccessStatusCode, "révocation isolée des liens");
    var otherBase = other.Link.GetLeftPart(UriPartial.Path);
    await File.AppendAllTextAsync(file, " modifié");
    Check((await http.GetAsync(otherBase + "/files/0")).StatusCode == HttpStatusCode.Conflict, "source modifiée signalée");
    File.Delete(twin);
    Check((await http.GetAsync(otherBase + "/all.zip")).StatusCode == HttpStatusCode.Conflict, "ZIP refusé proprement si une source manque");

    var movie = Path.Combine(folder.FullName, "video.bin");
    using (var generate = File.Create(movie)) generate.SetLength(64L * 1024 * 1024);
    var big = catalog.Add(SharedFile.Select([movie]), TimeSpan.FromMinutes(5), ShareScope.Local, origin, server.Fingerprint, "Local");
    using (var ongoing = await http.GetAsync(big.Link.GetLeftPart(UriPartial.Path) + "/files/0", HttpCompletionOption.ResponseHeadersRead))
    {
        await using var stream = await ongoing.Content.ReadAsStreamAsync();
        var first = new byte[4096];
        Check(await stream.ReadAsync(first) > 0, "début du transfert d’un fichier volumineux");
        catalog.Revoke(big.Token);
        await Throws<IOException>(() => stream.CopyToAsync(Stream.Null), "transfert en cours interrompu par révocation");
    }

    Check(!NetworkInfo.IsPublic(IPAddress.Parse("100.100.1.1")) && !NetworkInfo.IsPublic(IPAddress.Parse("192.168.1.9")), "détection des IPv4 partagées et privées");
    Check(NetworkInfo.IsPublic(IPAddress.Parse("2606:4700::1")) && !NetworkInfo.IsPublic(IPAddress.Parse("fd00::1")), "distinction IPv6 publique et locale");
    Check(NetworkInfo.ManualOrigin("partage.example", 47831)?.Port == 47831 && NetworkInfo.ManualOrigin("partage.example:443", 47831)?.Port == 443, "domaine manuel et port externe explicite");
    Check(NetworkInfo.ManualOrigin("[2606:4700::1]:5443", 47831)?.Port == 5443, "saisie IPv6 avec port externe");
    await Throws<FormatException>(() => Task.FromResult(NetworkInfo.ManualOrigin("192.168.0.3", 47831)), "une IPv4 privée ne produit pas de lien Internet");
    var router = new TestRouter(NatProtocol.Upnp);
    var existing = new Mapping(Protocol.Tcp, 47831, 47831, 600, "Autre application");
    router.Rules.Add(47831, existing);
    var lease = await RouterLease.CreateAsync(router, router.External, 47831, default);
    Check(lease.Origin.Port != 47831 && router.Rules[47831] == existing && router.Rules[lease.Origin.Port].Lifetime > 0, "UPnP utilise un port libre et un bail fini");
    Check(await lease.RenewAsync(), "renouvellement du bail en gardant le lien distribué");
    await lease.ReleaseAsync();
    Check(router.Rules.Count == 1 && router.Rules[47831] == existing, "fermeture de la seule redirection créée par Passload");
    var pmp = new TestRouter(NatProtocol.Pmp) { AssignedPort = 54321 };
    var pmpLease = await RouterLease.CreateAsync(pmp, pmp.External, 47831, default);
    Check(pmpLease.Origin.Port == 54321, "respect du port attribué par NAT-PMP");
    pmp.External = IPAddress.Parse("203.0.113.11");
    Check(!await pmpLease.RenewAsync(), "changement d’adresse WAN détecté");
    await pmpLease.ReleaseAsync();
    Check(pmp.Rules.Count == 0, "suppression du bail NAT-PMP");
    if (args.Contains("--network"))
    {
        await using var service = new ShareService();
        var local = await service.CreateAsync([file], TimeSpan.FromMinutes(1), ShareScope.Local, NetworkInfo.DefaultPort, null);
        using var localClient = new ReceiveClient(local.Link.AbsoluteUri);
        Check((await localClient.InspectAsync(default)).Files.Length == 1, "adresse de l’interface réseau réellement accessible depuis ce PC");
        await service.RevokeAsync(local.Token);
        await Throws<HttpRequestException>(() => localClient.InspectAsync(default), "arrêt du serveur après le dernier lien");
        try
        {
            var internet = await service.CreateAsync([empty], TimeSpan.FromMinutes(1), ShareScope.Internet, NetworkInfo.DefaultPort, null);
            Console.WriteLine("Route Internet détectée sur ce PC : " + internet.RouteLabel);
            Check(internet.Scope == ShareScope.Internet && internet.Link.Scheme == "https", "construction du lien Internet sur le réseau réel");
            await service.RevokeAsync(internet.Token);
        }
        catch (InvalidOperationException ex) { Console.WriteLine("Diagnostic du réseau réel : " + ex.Message); }
    }
    Console.WriteLine($"\n{passed} vérifications réussies.");
}
finally
{
    catalog.EndAll();
    var root = Path.GetFullPath(Path.GetTempPath());
    if (folder.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar) == root.TrimEnd(Path.DirectorySeparatorChar) && folder.Name.StartsWith("passload-check-", StringComparison.Ordinal))
        folder.Delete(recursive: true);
}
