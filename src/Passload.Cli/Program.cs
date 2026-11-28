using System.Globalization;
using System.Runtime.InteropServices;
using Passload.Cli;
using Passload.Core;

Locale.Use(Environment.GetEnvironmentVariable("PASSLOAD_LANGUAGE"));

if (args is [] or ["--help"] or ["-h"])
{
    Console.WriteLine($"""
        {Locale.Text("CliIntro")}

        passload [options] {Locale.Text("CliFileArg")} [{Locale.Text("CliFileArg")} ...]
        passload --receive {Locale.Text("CliLinkArg")} {Locale.Text("CliDestinationArg")}
        passload --check-update

        --local              {Locale.Text("CliLocal")}
        --duration 1h        {Locale.Text("CliDuration")}
        --port 47831         {Locale.Text("CliPort")}
        --address URL        {Locale.Text("CliAddress")}
        --domain example.org {Locale.Text("CliDomain")}
        --title "..."        {Locale.Text("CliTitle")}
        --description "..."  {Locale.Text("CliDescription")}

        {Locale.Text("CliKeepOpen")}
        """);
    return 0;
}
if (args is ["--version"])
{
    Console.WriteLine(GitHubUpdates.CurrentVersion.ToString(3));
    return 0;
}

if (args is ["--interactive"])
{
    Console.WriteLine(Locale.Text("CliInteractive"));
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
            if (++i >= args.Length) throw new ArgumentException(Locale.Text("CliMissingValue", option));
            var value = args[i];
            switch (option)
            {
                case "--duration": duration = ParseDuration(value); break;
                case "--port":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535)
                        throw new ArgumentException(Locale.Text("InvalidPort"));
                    break;
                case "--address": address = value; break;
                case "--domain": domain = value; break;
                case "--title": title = value; break;
                case "--description": description = value; break;
                default: throw new ArgumentException(Locale.Text("CliUnknownOption", option));
            }
        }
    }

    if (files.Count == 0) throw new ArgumentException(Locale.Text("CliNoFile"));
    await using var sendUpdates = PortableUpdates.Start(stop.Token);
    await using var sharing = new ShareService();
    var share = await sharing.CreateAsync(files, duration, scope, port, address, stop.Token, publicHost: domain);
    share.UpdateDetails(title, description);
    Console.WriteLine(share.PublishedLink.AbsoluteUri);
    Console.Error.WriteLine(Locale.Text("CliSharingActive"));

    using var clock = new PeriodicTimer(TimeSpan.FromSeconds(2));
    while (share.Active && await clock.WaitForNextTickAsync(stop.Token))
    {
        if (await sharing.MaintainAsync()) throw new IOException(Locale.Text("ForwardingChanged"));
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
    throw new ArgumentException(Locale.Text("CliInvalidDuration"));
}
