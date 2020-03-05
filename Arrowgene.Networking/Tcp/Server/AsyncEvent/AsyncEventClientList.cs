using System.Collections.Generic;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    /// <summary>
    /// Concurrent List implementation by Lock
    /// </summary>
    public class AsyncEventClientList<T>
    {
        private readonly List<T> _list;
        private readonly object _lock;

        public AsyncEventClientList()
        {
            _lock = new object();
            _list = new List<T>();
        }

        public AsyncEventClientList(IEnumerable<T> collection) : this()
        {
            _list.AddRange(collection);
        }

        /// <summary>
        /// Returns a new list that holds the elements in the list at the time of the snapshot.
        /// </summary>
        public List<T> Snapshot()
        {
            lock (_lock)
            {
                return new List<T>(_list);
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _list.Count;
                }
            }
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                _list.Add(item);
            }
        }

        /// <summary>
        /// Adds the entry if it not exists.
        /// </summary>
        public bool AddIfNotExist(T item)
        {
            lock (_lock)
            {
                if (!_list.Contains(item))
                {
                    _list.Add(item);
                    return true;
                }
            }

            return false;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _list.Clear();
            }
        }

        public bool Remove(T item)
        {
            lock (_lock)
            {
                return _list.Remove(item);
            }
        }

        public T this[int index]
        {
            get
            {
                lock (_lock)
                {
                    return _list[index];
                }
            }
            set
            {
                lock (_lock)
                {
                    _list[index] = value;
                }
            }
        }
    }
}