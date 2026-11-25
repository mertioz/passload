using System.Globalization;
using System.Runtime.InteropServices;
using Passload.Cli;
using Passload.Core;

const string help = """
    Passload partage des fichiers depuis cet appareil.

    passload [options] fichier [fichier ...]
    passload --receive lien destination
    passload --check-update

    --local             Partage HTTPS sur le réseau local
    --duration 1h       Validité du lien, en minutes (m), heures (h) ou jours (d)
    --port 47831        Port TCP local
    --address URL       Adresse HTTPS d’une redirection configurée manuellement
    --domain NOM        Domaine couvert par un certificat serveur installé
    --title TEXTE       Titre affiché sur la page de téléchargement
    --description TEXTE Description affichée sur la page de téléchargement

    Laissez le programme ouvert pendant le partage. Ctrl+C ferme le lien.
    """;

if (args is [] or ["--help"] or ["-h"])
{
    Console.WriteLine(help);
    return 0;
}
if (args is ["--version"])
{
    Console.WriteLine(GitHubUpdates.CurrentVersion.ToString(3));
    return 0;
}

if (args is ["--interactive"])
{
    Console.WriteLine("Saisissez un chemin de fichier par ligne. Validez une ligne vide pour créer le lien.");
    var selected = new List<string>();
    while (Console.ReadLine() is { Length: > 0 } file)
        selected.Add(file.Trim().Trim('"'));
    if (selected.Count == 0) return 0;
    args = ["--", .. selected];
}

using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += interrupt;
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    context => { context.Cancel = true; stop.Cancel(); });

try
{
    if (args is ["--check-update"])
    {
        await PortableUpdates.CheckNowAsync(stop.Token);
        return 0;
    }
    if (args is ["--receive", var link, var destination])
    {
        await using var updates = PortableUpdates.Start(stop.Token);
        using var client = new ReceiveClient(link);
        var manifest = await client.InspectAsync(stop.Token);
        await client.DownloadAsync(manifest.Files.Length == 1 ? manifest.Files[0] : null, destination, null, stop.Token);
        return 0;
    }

    var scope = ShareScope.Internet;
    var port = NetworkInfo.DefaultPort;
    var duration = TimeSpan.FromHours(1);
    string? address = null, domain = null, title = null, description = null;
    var files = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        var option = args[i];
        if (option == "--")
        {
            files.AddRange(args[(i + 1)..]);
            break;
        }
        if (option == "--local") scope = ShareScope.Local;
        else if (!option.StartsWith("--", StringComparison.Ordinal)) files.Add(option);
        else
        {
            if (++i >= args.Length) throw new ArgumentException($"Valeur manquante pour {option}.");
            var value = args[i];
            switch (option)
            {
                case "--duration": duration = ParseDuration(value); break;
                case "--port":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535)
                        throw new ArgumentException("Indiquez un port entre 1024 et 65535.");
                    break;
                case "--address": address = value; break;
                case "--domain": domain = value; break;
                case "--title": title = value; break;
                case "--description": description = value; break;
                default: throw new ArgumentException($"Option inconnue {option}. Utilisez --help.");
            }
        }
    }

    if (files.Count == 0) throw new ArgumentException("Indiquez au moins un fichier. Utilisez --help.");
    await using var sendUpdates = PortableUpdates.Start(stop.Token);
    await using var sharing = new ShareService();
    var share = await sharing.CreateAsync(files, duration, scope, port, address, stop.Token, publicHost: domain);
    share.UpdateDetails(title, description);
    Console.WriteLine(share.PublishedLink.AbsoluteUri);
    Console.Error.WriteLine("Partage HTTPS actif. Ctrl+C ferme le lien.");

    using var clock = new PeriodicTimer(TimeSpan.FromSeconds(2));
    while (share.Active && await clock.WaitForNextTickAsync(stop.Token))
    {
        if (await sharing.MaintainAsync()) throw new IOException("La redirection de la box a changé. Créez un nouveau lien.");
    }
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
finally { Console.CancelKeyPress -= interrupt; }

static TimeSpan ParseDuration(string value)
{
    if (value.Length > 1 && int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count <= 10080)
    {
        var duration = value[^1] switch
        {
            'm' => TimeSpan.FromMinutes(count),
            'h' => TimeSpan.FromHours(count),
            'd' => TimeSpan.FromDays(count),
            _ => TimeSpan.Zero
        };
        if (duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(7)) return duration;
    }
    throw new ArgumentException("Choisissez une durée de 1m à 7d, par exemple 15m ou 1h.");
}
