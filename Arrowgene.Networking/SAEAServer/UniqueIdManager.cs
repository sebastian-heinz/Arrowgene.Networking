using System;

namespace Arrowgene.Networking.SAEAServer;


public static class UniqueIdManager
{
    // 17 bits supports ClientIDs up to 131,072
    private const int ID_BIT_SHIFTS = 17;
    private const int ID_MASK = (1 << ID_BIT_SHIFTS) - 1; // 0x1FFFF
        
    /// <summary>
    /// Combines ClientID and Generation into a single 64-bit integer.
    /// </summary>
    public static long Pack(int clientId, long generation)
    {
        // Safety check: Ensure ClientID fits in 17 bits
        if (clientId > 100000 || clientId < 0) 
            throw new ArgumentOutOfRangeException(nameof(clientId), "ID must be 0-100,000");
        
        return (generation << ID_BIT_SHIFTS) | (long)(clientId & ID_MASK);
    }
        
    public static int GetClientId(long uniqueId)
    {
        return (int)(uniqueId & ID_MASK);
    }
        
    public static long GetGeneration(long uniqueId)
    {
        return uniqueId >> ID_BIT_SHIFTS;
    }
}