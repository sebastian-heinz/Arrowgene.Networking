using System;
using System.Net.Sockets;
using System.Runtime.Serialization;

namespace Arrowgene.Networking
{
    /// <summary>
    /// Represents an additional raw socket option applied by <see cref="SocketSettings"/>.
    /// </summary>
    [DataContract]
    public class SocketOption : ICloneable, IEquatable<SocketOption>
    {
        /// <summary>
        /// Gets or sets the serialized socket option level name used by the data contract.
        /// </summary>
        [DataMember(Name = "Level", Order = 0)]
        public string DataLevel
        {
            get => Level.ToString();
            set => Level = ParseEnum<SocketOptionLevel>(value);
        }

        /// <summary>
        /// Gets or sets the socket option name.
        /// </summary>
        [DataMember(Order = 1)]
        public SocketOptionName Name { get; set; }

        /// <summary>
        /// Gets or sets the socket option value.
        /// </summary>
        [DataMember(Order = 2)]
        public object Value { get; set; }

        /// <summary>
        /// Gets or sets the socket option level.
        /// </summary>
        [IgnoreDataMember]
        public SocketOptionLevel Level { get; set; }

        /// <summary>
        /// Initializes a socket option definition.
        /// </summary>
        /// <param name="level">The option level.</param>
        /// <param name="name">The option name.</param>
        /// <param name="value">The option value.</param>
        public SocketOption(SocketOptionLevel level, SocketOptionName name, object value)
        {
            Level = level;
            Name = name;
            Value = value;
        }

        /// <summary>
        /// Initializes a copy of an existing socket option.
        /// </summary>
        /// <param name="socketOption">The option to copy.</param>
        public SocketOption(SocketOption socketOption)
        {
            Level = socketOption.Level;
            Name = socketOption.Name;
            Value = socketOption.Value;
        }

        /// <summary>
        /// Creates a copy of the socket option.
        /// </summary>
        /// <returns>A copied <see cref="SocketOption"/> instance.</returns>
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

        /// <summary>
        /// Compares this socket option with another instance.
        /// </summary>
        /// <param name="other">The option to compare.</param>
        /// <returns><c>true</c> if both options describe the same socket option.</returns>
        public bool Equals(SocketOption other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Value, other.Value) && Level == other.Level && Name == other.Name;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((SocketOption) obj);
        }

        /// <inheritdoc />
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
