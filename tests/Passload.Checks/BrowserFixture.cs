using System.Text.Json;
using Passload.Core;

namespace Passload.Checks;

internal static class BrowserFixture
{
    // The browser test reads a temporary link for synthetic files from stdout.
    public static async Task RunAsync(string directory)
    {
        string[] names = ["probe.txt", "probe.bin"];
        var files = SharedFile.Select(names.Select(n => Path.Combine(directory,n)));
        var catalog = new ShareCatalog();
        try
        {
            await using var server = await TransferServer.StartAsync(catalog,0,[],loopbackOnly:true,browserEngine:true);
            await using var https = await TransferServer.StartAsync(catalog, 0, [], loopbackOnly:true);
            var duration = TimeSpan.FromMinutes(9);
            var origin = new Uri($"http://127.0.0.1:{server.Port}");
            var viewer = new Uri($"https://127.0.0.1:{https.Port}");
            var share = catalog.Add(files, duration, ShareScope.Internet, viewer, https.Fingerprint, "Internet", publicWebRtc: true);
            var message = new { op = "start", token = share.Token, until = share.ExpiresAt.ToUnixTimeMilliseconds() };
            var init = new {page = new Uri(origin, "/engine/host").AbsoluteUri, link = share.Link.AbsoluteUri, message,
                sharedLink = share.PublishedLink.AbsoluteUri, sharedMessage = share.WebRtcStartMessage(sharedPage:true)};
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(init));
            await Console.Out.FlushAsync();
            await Console.In.ReadLineAsync();
        }
        finally { catalog.EndAll(); }
    }
}
