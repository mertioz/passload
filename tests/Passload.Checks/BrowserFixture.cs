using System.Text.Json;
using Passload.Core;

namespace Passload.Checks;

internal static class BrowserFixture
{
    // Used by the two-machine browser test. The private configuration goes to the
    // parent process over a local pipe. Only fixed, public test fixtures are served.
    public static async Task RunAsync(string directory, bool publicWebRtc = false)
    {
        string[] names = publicWebRtc ? ["probe.txt", "probe.bin"] : ["notes-test.txt", "donnees-test.bin", "vide.txt"];
        var files = SharedFile.Select(names.Select(n => Path.Combine(directory,n)));
        var catalog = new ShareCatalog();
        try
        {
            await using var server = await TransferServer.StartAsync(catalog,0,[],loopbackOnly:true,browserEngine:true);
            var duration = TimeSpan.FromMinutes(9);
            var origin = new Uri($"http://127.0.0.1:{server.Port}");
            var viewer = publicWebRtc ? origin : new Uri("https://mertioz.github.io/passload/recevoir/");
            var invite = publicWebRtc ? null : InternetInvitation.Create(viewer, duration);
            var share = catalog.Add(files, duration, ShareScope.Internet, viewer, "", "Internet",
                publicLink:invite?.Link, publicWebRtc:publicWebRtc);
            var message = invite?.StartMessage(share.Token, true) ??
                new { op = "start", token = share.Token, until = share.ExpiresAt.ToUnixTimeMilliseconds(), publicRtc = true };
            var init = new {page = new Uri(origin, "/engine/host").AbsoluteUri, link = share.Link.AbsoluteUri, message};
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(init));
            await Console.Out.FlushAsync();
            await Console.In.ReadLineAsync();
        }
        finally { catalog.EndAll(); }
    }
}
