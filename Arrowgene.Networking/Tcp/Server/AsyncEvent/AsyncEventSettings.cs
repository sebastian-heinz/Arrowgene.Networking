using System;
using System.Runtime.Serialization;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    [DataContract]
    public class AsyncEventSettings : ICloneable
    {
        [DataMember(Order = 0)] public string Identity { get; set; }

        [DataMember(Order = 1)] public int MaxConnections { get; set; }

        [DataMember(Order = 3)] public int BufferSize { get; set; }

        [DataMember(Order = 4)] public int Retries { get; set; }

        [DataMember(Order = 5)] public int MaxUnitOfOrder { get; set; }

        [DataMember(Order = 9)] public int SocketTimeoutSeconds { get; set; }

        [DataMember(Order = 10)] public SocketSettings SocketSettings { get; set; }
        
        [DataMember(Order = 11)] public bool DebugMode { get; set; }

        public AsyncEventSettings()
        {
            BufferSize = 2000;
            MaxConnections = 100;
            Retries = 10;
            SocketSettings = new SocketSettings();
            Identity = "";
            MaxUnitOfOrder = 1;
            SocketTimeoutSeconds = -1;
            DebugMode = false;
        }

        public AsyncEventSettings(AsyncEventSettings settings)
        {
            Identity = settings.Identity;
            BufferSize = settings.BufferSize;
            MaxConnections = settings.MaxConnections;
            Retries = settings.Retries;
            SocketSettings = new SocketSettings(settings.SocketSettings);
            MaxUnitOfOrder = settings.MaxUnitOfOrder;
            SocketTimeoutSeconds = settings.SocketTimeoutSeconds;
            DebugMode = settings.DebugMode;
        }

        public object Clone()
        {
            return new AsyncEventSettings(this);
        }

        public void Validate()
        {
            if (MaxUnitOfOrder <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxUnitOfOrder), "MaxUnitOfOrder must be greater than zero.");
            }
        }
    }
}
