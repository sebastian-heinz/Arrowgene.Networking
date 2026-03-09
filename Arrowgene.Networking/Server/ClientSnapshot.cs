using System;
using System.Net;

namespace Arrowgene.Networking.Server;

public readonly record struct ClientSnapshot(
    int ClientId,
    uint Generation,
    string Identity,
    IPAddress RemoteIpAddress,
    ushort Port,
    bool IsAlive,
    DateTime ConnectedAt,
    long LastReadMs,
    long LastWriteMs,
    ulong BytesReceived,
    ulong BytesSent,
    int PendingOperations
);