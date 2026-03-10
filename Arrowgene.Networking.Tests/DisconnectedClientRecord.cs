using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Tests;

internal readonly record struct DisconnectedClientRecord(
    ClientSnapshot Snapshot
);
