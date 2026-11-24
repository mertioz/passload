using System.Net;
using Mono.Nat;

namespace Passload.Checks;

internal sealed class TestRouter(NatProtocol protocol) : INatDevice
{
    public Dictionary<int, Mapping> Rules { get; } = new();
    public int? AssignedPort { get; set; }
    public IPAddress External { get; set; } = IPAddress.Parse("203.0.113.10");
    public IPEndPoint DeviceEndpoint { get; } = new(IPAddress.Loopback, 5351);
    public DateTime LastSeen => DateTime.UtcNow;
    public NatProtocol NatProtocol => protocol;
    public Task<IPAddress> GetExternalIPAsync() => Task.FromResult(External);
    public Task<Mapping[]> GetAllMappingsAsync() => Task.FromResult(Rules.Values.ToArray());
    public Task<Mapping> GetSpecificMappingAsync(Protocol _, int port) => Rules.TryGetValue(port, out var value)
        ? Task.FromResult(value) : Task.FromException<Mapping>(new MappingException((ErrorCode)714, "NoSuchEntryInArray"));
    public Task<Mapping> CreatePortMapAsync(Mapping m)
    {
        var assigned = new Mapping(m.Protocol, m.PrivatePort, AssignedPort ?? m.PublicPort, m.Lifetime, m.Description);
        Rules[assigned.PublicPort] = assigned;
        return Task.FromResult(assigned);
    }
    public Task<Mapping> DeletePortMapAsync(Mapping m) { Rules.Remove(m.PublicPort); return Task.FromResult(m); }
}
