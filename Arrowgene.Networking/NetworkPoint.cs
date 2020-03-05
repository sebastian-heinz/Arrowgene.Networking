using System;
using System.Net;
using System.Runtime.Serialization;

namespace Arrowgene.Networking
{
    [DataContract]
    public class NetworkPoint : ICloneable, IEquatable<NetworkPoint>

    {
        [IgnoreDataMember]
        public IPAddress Address { get; set; }

        [DataMember(Name = "Address", Order = 0)]
        public string DataPublicIpAddress
        {
            get => Address.ToString();
            set => Address = string.IsNullOrEmpty(value) ? null : IPAddress.Parse(value);
        }

        [DataMember(Order = 1)]
        public ushort Port { get; set; }

        public NetworkPoint(IPAddress address, ushort port)
        {
            Address = address;
            Port = port;
        }

        public NetworkPoint(NetworkPoint networkPoint)
        {
            string ip = networkPoint.Address.ToString();
            Address = string.IsNullOrEmpty(ip) ? null : IPAddress.Parse(ip);
            Port = networkPoint.Port;
        }

        public NetworkPoint(IPEndPoint ipEndPoint)
        {
            Address = ipEndPoint.Address;
            Port = (ushort) ipEndPoint.Port;
        }

        public IPEndPoint ToIpEndPoint()
        {
            return new IPEndPoint(Address, Port);
        }

        public object Clone()
        {
            return new NetworkPoint(this);
        }

        public bool Equals(NetworkPoint other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return other.DataPublicIpAddress == DataPublicIpAddress && other.Port == Port;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((NetworkPoint) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((DataPublicIpAddress != null ? DataPublicIpAddress.GetHashCode() : 0) * 397) ^ Port.GetHashCode();
            }
        }
    }
}