namespace Unosquare.FFplayDotNet.Core
{
    using System.Collections.Generic;

    /// <summary>
    /// Represents a very simple dictionary wrapper
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <seealso cref="System.Collections.Generic.Dictionary{TKey, TValue}" />
    internal sealed class ObjectDictionary<TKey, TValue>
        : Dictionary<TKey, TValue>
    {

        private readonly object SyncRoot = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectDictionary{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="initialCapacity">The initial capacity.</param>
        public ObjectDictionary(int initialCapacity)
            : base(initialCapacity)
        {

        }

        /// <summary>
        /// Gets or sets the <see cref="TValue"/> with the specified key.
        /// return the default value of the value type when the key does not exist.
        /// </summary>
        /// <value>
        /// The <see cref="TValue"/>.
        /// </value>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        public new TValue this[TKey key]
        {
            get
            {
                lock (SyncRoot)
                {
                    if (ContainsKey(key) == false)
                        return default(TValue);

                    return base[key];
                }

            }
            set
            {
                lock (SyncRoot)
                {
                    base[key] = value;
                }
            }
        }
    }
}
