using System;
using System.Net.Sockets;
using System.Runtime.Serialization;

namespace Arrowgene.Networking
{
    [DataContract]
    public class SocketOption : ICloneable, IEquatable<SocketOption>
    {
        [DataMember(Name = "Level", Order = 0)]
        public string DataLevel
        {
            get => Level.ToString();
            set => Level = ParseEnum<SocketOptionLevel>(value);
        }
        
        [DataMember(Order = 1)]
        public SocketOptionName Name { get; set; }

        [DataMember(Order = 2)]
        public object Value { get; set; }

        [IgnoreDataMember]
        public SocketOptionLevel Level { get; set; }

        public SocketOption(SocketOptionLevel level, SocketOptionName name, object value)
        {
            Level = level;
            Name = name;
            Value = value;
        }

        public SocketOption(SocketOption socketOption)
        {
            Level = socketOption.Level;
            Name = socketOption.Name;
            Value = socketOption.Value;
        }

        public object Clone()
        {
            return new SocketOption(this);
        }

        private T ParseEnum<T>(string value) where T : struct
        {
            if (!Enum.TryParse(value, true, out T instance))
            {
                instance = default(T);
            }

            return instance;
        }

        public bool Equals(SocketOption other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Value, other.Value) && Level == other.Level && Name == other.Name;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((SocketOption) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (Value != null ? Value.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (int) Level;
                hashCode = (hashCode * 397) ^ (int) Name;
                return hashCode;
            }
        }
    }
}