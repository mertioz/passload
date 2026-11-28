using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Mono.Nat;
using Passload.Checks;
using Passload.Core;

if (args is ["--direct-check"])
{
    await DirectLinkCheck.RunAsync();
    return;
}

if (args is ["--public-rtc-fixture", var rtcDirectory])
{
    await BrowserFixture.RunAsync(rtcDirectory);
    return;
}

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
    http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR");
    var page = await http.GetStringAsync(address);
    Check(WebUtility.HtmlDecode(page).Contains("Tout télécharger en ZIP") && WebUtility.HtmlDecode(page).Contains("été & lumière.txt"), "page web et noms accentués");
    Check(!page.Contains(folder.FullName, StringComparison.OrdinalIgnoreCase) && !page.Contains(share.Link.Fragment), "chemins locaux et empreinte absents de la page");
    foreach (var locale in Locale.Supported)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.AcceptLanguage.ParseAdd($"it-IT, {locale.Name};q=0.9, fr;q=0.4");
        using var reply = await http.SendAsync(request);
        var localized = WebUtility.HtmlDecode(await reply.Content.ReadAsStringAsync());
        Check(reply.IsSuccessStatusCode && localized.Contains($"lang=\"{locale.Name}\"") &&
            localized.Contains(locale.Web("DownloadZip")) && localized.Contains("été & lumière.txt"),
            $"page de réception localisée ({locale.Name})");
    }
    share.UpdateDetails("Une sélection <privée>", "Une description <script>\nDeuxième ligne.");
    var decorated = await http.GetStringAsync(address);
    Check(share.Link.GetLeftPart(UriPartial.Path) == address && WebUtility.HtmlDecode(decorated).Contains("<title>Une sélection <privée></title>"),
        "l’adresse de partage fonctionne après le renommage");
    Check(!decorated.Contains("<header>") && !decorated.Contains("<script>") &&
        WebUtility.HtmlDecode(decorated).Contains("Une description <script>\nDeuxième ligne."),
        "page sans bandeau et description échappée");
    using (var metadata = new ReceiveClient(share.Link.AbsoluteUri))
    {
        var details = await metadata.InspectAsync(default);
        Check(details.Name == "Une sélection <privée>" && details.Description == "Une description <script>\nDeuxième ligne.",
            "titre et description visibles dans le manifeste de réception");
    }
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
    Check(NetworkInfo.NormalizePublicHost("  Démo.Exemple.FR. ") == "xn--dmo-bma.exemple.fr" &&
        NetworkInfo.NormalizePublicHost("") is null, "normalisation d’un domaine HTTPS facultatif");
    await Throws<FormatException>(() => Task.Run(() => NetworkInfo.NormalizePublicHost("https://exemple.fr/s")),
        "une URL complète ne remplace pas le nom du certificat");
    await Throws<FormatException>(() => Task.FromResult(NetworkInfo.ManualOrigin("192.168.0.3", 47831)), "une IPv4 privée ne produit pas de lien Internet");
    var router = new TestRouter(NatProtocol.Upnp);
    var existing = new Mapping(Protocol.Tcp, 47831, 47831, 600, "Autre application");
    router.Rules.Add(47831, existing);
    var lease = await RouterLease.CreateAsync(router, router.External, 47831, default);
    Check(lease.Origin.Port != 47831 && router.Rules[47831] == existing && router.Rules[lease.Origin.Port].Lifetime > 0, "UPnP utilise un port libre et un bail fini");
    Check(await lease.RenewAsync(), "renouvellement du bail en gardant le lien distribué");
    await lease.ReleaseAsync();
    Check(router.Rules.Count == 1 && router.Rules[47831] == existing, "fermeture de la seule redirection créée par Passload");
    var restricted = new TestRouter(NatProtocol.Upnp) { AcceptsPort = port => port is >= 1024 and < 16384 };
    var alternative = await RouterLease.CreateAsync(restricted, restricted.External, 47831, default);
    var rule = restricted.Rules[alternative.Origin.Port];
    Check(restricted.RequestedPorts[0] == 47831 && restricted.RequestedPorts.Count == 5 &&
        alternative.Origin.Port < 16384 && rule.PrivatePort == 47831,
        "UPnP retente dans les autres plages et publie le port externe attribué");
    await alternative.ReleaseAsync();
    Check(restricted.Rules.Count == 0, "fermeture du port de remplacement");
    var permanent = new TestRouter(NatProtocol.Upnp) { ReportsPermanentLease = true };
    await Throws<IOException>(() => RouterLease.CreateAsync(permanent, permanent.External, 47831, default), "une redirection permanente n’est pas publiée");
    Check(permanent.Rules.Count == 0, "suppression de la redirection créée à tort comme permanente");
    var gateway = IPAddress.Parse("192.168.1.1");
    byte[] Reply(string url) => System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nLOCATION: {url}\r\n\r\n");
    Check(UpnpDiscovery.GatewayLocation(Reply("http://192.168.1.1:1900/igd.xml"), gateway) is not null,
        "la découverte SSDP accepte l’adresse du routeur");
    Check(UpnpDiscovery.GatewayLocation(Reply("http://192.168.1.2:1900/igd.xml"), gateway) is null,
        "la découverte SSDP écarte les autres hôtes");
    var pmp = new TestRouter(NatProtocol.Pmp) { AssignedPort = 54321 };
    var pmpLease = await RouterLease.CreateAsync(pmp, pmp.External, 47831, default);
    Check(pmpLease.Origin.Port == 54321, "respect du port attribué par NAT-PMP");
    pmp.External = IPAddress.Parse("203.0.113.11");
    Check(!await pmpLease.RenewAsync(), "changement d’adresse WAN détecté");
    await pmpLease.ReleaseAsync();
    Check(pmp.Rules.Count == 0, "suppression du bail NAT-PMP");
    var privateCatalog = new ShareCatalog();
    await using (var engine = await TransferServer.StartAsync(privateCatalog,0,[],loopbackOnly:true,browserEngine:true))
    {
        var localOrigin = new Uri($"http://127.0.0.1:{engine.Port}");
        var preview = privateCatalog.Add(SharedFile.Select([file]),TimeSpan.FromMinutes(1),ShareScope.Internet,localOrigin,"","Internet");
        using var browser = new HttpClient {BaseAddress=localOrigin};
        browser.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR");
        var localBase = $"/s/{preview.Token}";
        Check(WebUtility.HtmlDecode(await browser.GetStringAsync(localBase)).Contains("Un fichier partagé"),
            "aperçu sur le PC expéditeur sans rebouclage de la box");
        Check((await browser.GetStringAsync(localBase+"/files/0")) == await File.ReadAllTextAsync(file), "fichier accessible par l’aperçu local");
        foreach (var asset in new[]{"","receive.js","incoming.js","wire.js","pipe.js","save.js","disk.js","style.css"})
            Check((await browser.GetAsync("/recevoir/"+asset)).IsSuccessStatusCode, "lecteur intégré disponible · "+(asset==""?"page":asset));
        using var wrongHost = new HttpRequestMessage(HttpMethod.Get,localBase);
        wrongHost.Headers.Host = "autre.example";
        Check((await browser.SendAsync(wrongHost)).StatusCode==HttpStatusCode.Forbidden, "moteur local limité à sa propre adresse");
        privateCatalog.Revoke(preview.Token);
        Check((await browser.GetAsync(localBase)).StatusCode==HttpStatusCode.NotFound, "aperçu local révoqué avec le partage");

    }
    var rtc = catalog.Add(SharedFile.Select([file]), TimeSpan.FromMinutes(1), ShareScope.Local, origin, server.Fingerprint, "WebRTC", publicWebRtc:true);
    var publicShare = catalog.Add(SharedFile.Select([file]), TimeSpan.FromMinutes(1), ShareScope.Internet, origin, server.Fingerprint, "WebRTC", publicWebRtc:true);
    Check(publicShare.PublishedLink.Host == "transfer.aymen.site" && publicShare.PublishedLink.AbsolutePath == "/" &&
        publicShare.PublishedLink.Fragment.Split('.').Length == 5 && ShareLinks.DirectAddress(publicShare.PublishedLink.AbsoluteUri) == publicShare.Link.AbsoluteUri,
        "adresse WebRTC commune et secours direct vérifiable");
    var httpsShare = catalog.Add(SharedFile.Select([file]), TimeSpan.FromMinutes(1), ShareScope.Internet, origin, server.Fingerprint, "HTTPS");
    using (var portalReceiver = new ReceiveClient(httpsShare.PublishedLink.AbsoluteUri))
    {
        Check(httpsShare.PublishedLink.Host == "transfer.aymen.site" && httpsShare.PublishedLink.Fragment.Split('.').Length == 3 &&
            (await portalReceiver.InspectAsync(default)).Files.Length == 1, "lien HTTPS commun reconnu par la réception native");
    }
    Check(rtc.PublishedLink == rtc.Link, "lien local distribué directement sur le réseau");
    await using (var engine = await TransferServer.StartAsync(new ShareCatalog(), 0, [], loopbackOnly:true,
        browserEngine:true, publicShares:catalog.Find))
    {
        var rtcBase = rtc.Link.GetLeftPart(UriPartial.Path);
        Check((await http.GetStringAsync(rtcBase)).Contains("/assets/public-receive.js"), "lecteur WebRTC servi sur l’adresse du partage");
        Check((await http.GetAsync(rtcBase+"/assets/wire.js")).IsSuccessStatusCode, "module WebRTC fourni par le PC expéditeur");
        Check((await http.GetAsync(rtcBase+"/assets/pipe.js")).StatusCode == HttpStatusCode.NotFound, "les modules de relais ne sont pas exposés par le lien IP");
        Check((await http.GetAsync(new Uri(origin, $"/engine/rtc/{rtc.Token}"))).StatusCode == HttpStatusCode.NotFound, "la prise hôte est inaccessible sur le port public");
        using var crossOrigin = new ClientWebSocket();
        crossOrigin.Options.CollectHttpResponseDetails = true;
        crossOrigin.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == server.Fingerprint;
        crossOrigin.Options.SetRequestHeader("Origin", "https://autre.example");
        var socket = new UriBuilder(rtcBase+"/rtc") { Scheme = "wss" }.Uri;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await Throws<WebSocketException>(() => crossOrigin.ConnectAsync(socket, time.Token), "une page d’une autre origine ne rejoint pas la signalisation");
        Check(crossOrigin.HttpStatusCode == HttpStatusCode.Forbidden, "origine de la signalisation contrôlée");
        using var host = new ClientWebSocket();
        host.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{engine.Port}");
        await host.ConnectAsync(new Uri($"ws://127.0.0.1:{engine.Port}/engine/rtc/{rtc.Token}"), time.Token);
        using var guest = new ClientWebSocket();
        guest.Options.HttpVersion = HttpVersion.Version20;
        guest.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        guest.Options.SetRequestHeader("Origin", origin.GetLeftPart(UriPartial.Authority));
        using var http2 = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new()
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == server.Fingerprint
            }
        });
        await guest.ConnectAsync(socket, http2, time.Token);
        var message = new byte[1024];
        var ready = await guest.ReceiveAsync(message.AsMemory(), time.Token);
        Check(guest.State == WebSocketState.Open && Encoding.UTF8.GetString(message, 0, ready.Count).Contains("\"op\":\"ready\""),
            "la signalisation WebSocket accepte CONNECT en HTTPS sur HTTP/2");
        catalog.Revoke(rtc.Token);
        Check((await http.GetAsync(rtcBase+"/assets/wire.js")).StatusCode == HttpStatusCode.NotFound, "signalisation et ressources révoquées avec le partage");
    }
    await DownloadHistoryCheck.RunAsync(folder.FullName, file, empty, Check, args.Contains("--network"));
    if (args.Contains("--network"))
    {
        using var reservation = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        reservation.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        reservation.Listen(1);
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        await using var service = new ShareService();
        var local = await service.CreateAsync([file], TimeSpan.FromMinutes(1), ShareScope.Local, port, null,
            details: new ShareDetails("  Dossier de test  ", "  Un aperçu du partage.\nDeuxième ligne.  "));
        Check(service.LocalPort == local.Link.Port && local.Link.Port != port, "port disponible choisi quand le port préféré est occupé");
        reservation.Dispose();
        using var localClient = new ReceiveClient(local.Link.AbsoluteUri);
        var initial = await localClient.InspectAsync(default);
        Check(initial.Files.Length == 1, "adresse de l’interface réseau réellement accessible depuis ce PC");
        Check(initial.Name == "Dossier de test" && initial.Description == "Un aperçu du partage.\nDeuxième ligne.",
            "nom et description visibles dès la création du lien");
        await service.RevokeAsync(local.Token);
        await Throws<HttpRequestException>(() => localClient.InspectAsync(default), "arrêt du serveur après le dernier lien");
        try
        {
            var internet = await service.CreateAsync([empty], TimeSpan.FromMinutes(1), ShareScope.Internet, port, null);
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
