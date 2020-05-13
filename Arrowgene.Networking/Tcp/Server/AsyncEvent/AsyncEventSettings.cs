using System;
using System.Runtime.Serialization;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    [DataContract]
    public class AsyncEventSettings : ICloneable
    {
        [DataMember(Order = 0)] public string Identity { get; set; }

        [DataMember(Order = 1)] public int MaxConnections { get; set; }

        [DataMember(Order = 2)] public int NumSimultaneouslyWriteOperations { get; set; }

        [DataMember(Order = 3)] public int BufferSize { get; set; }

        [DataMember(Order = 4)] public int Retries { get; set; }

        [DataMember(Order = 5)] public int MaxUnitOfOrder { get; set; }
        
        [DataMember(Order = 6)] public int MaxSimultaneousSendsPerClient { get; set; }

        [DataMember(Order = 9)] public int SocketTimeoutSeconds { get; set; }

        [DataMember(Order = 10)] public SocketSettings SocketSettings { get; set; }

        public AsyncEventSettings()
        {
            BufferSize = 2000;
            MaxConnections = 100;
            NumSimultaneouslyWriteOperations = 100;
            Retries = 10;
            SocketSettings = new SocketSettings();
            Identity = "";
            MaxUnitOfOrder = 1;
            MaxSimultaneousSendsPerClient = 1;
            SocketTimeoutSeconds = -1;
        }

        public AsyncEventSettings(AsyncEventSettings settings)
        {
            Identity = settings.Identity;
            BufferSize = settings.BufferSize;
            MaxConnections = settings.MaxConnections;
            NumSimultaneouslyWriteOperations = settings.NumSimultaneouslyWriteOperations;
            Retries = settings.Retries;
            SocketSettings = new SocketSettings(settings.SocketSettings);
            MaxUnitOfOrder = settings.MaxUnitOfOrder;
            MaxSimultaneousSendsPerClient = settings.NumSimultaneouslyWriteOperations;
            SocketTimeoutSeconds = settings.SocketTimeoutSeconds;
        }

        public object Clone()
        {
            return new AsyncEventSettings(this);
        }
    }
}
