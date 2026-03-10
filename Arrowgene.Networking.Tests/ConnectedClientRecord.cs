using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Tests;

internal readonly record struct ConnectedClientRecord(
    ClientHandle Handle,
    ClientKey Key,
    int UnitOfOrder
);
