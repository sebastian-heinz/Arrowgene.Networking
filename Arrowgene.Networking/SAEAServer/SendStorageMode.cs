using System.Runtime.Serialization;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// Selects how outbound send payloads are stored while queued.
/// </summary>
[DataContract]
public enum SendStorageMode
{
    /// <summary>
    /// Stores queued send payloads in runtime-managed arrays rented from <see cref="System.Buffers.ArrayPool{T}.Shared"/>.
    /// </summary>
    [EnumMember]
    Shared = 0,

    /// <summary>
    /// Stores queued send payloads in a per-client pinned ring buffer with no runtime allocations.
    /// </summary>
    [EnumMember]
    HardCapped = 1
}
