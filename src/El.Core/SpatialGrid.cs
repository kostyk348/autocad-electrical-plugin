using System;
using System.Collections.Generic;

namespace El.Core
{
    /// <summary>
    /// Пространственная сетка (uniform grid). Быстрый поиск «что рядом».
    /// Ячейка: (floor(x/cell)*cell, floor(y/cell)*cell).
    /// </summary>
    public sealed class SpatialGrid<T>
    {
        private readonly double _cell;
        private readonly Dictionary<long, List<T>> _buckets;

        public SpatialGrid(double cellSize)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            _cell = cellSize;
            _buckets = new Dictionary<long, List<T>>();
        }

        private long Key(double x, double y)
        {
            long cx = (long)Math.Floor(x / _cell);
            long cy = (long)Math.Floor(y / _cell);
            // хэш-свёртка пары координат (смешивание старших/младших битов)
            unchecked
            {
                long h = cx * 73856093L ^ cy * 19349663L;
                return h;
            }
        }

        public void Add(Point2D p, T item)
        {
            long k = Key(p.X, p.Y);
            if (!_buckets.TryGetValue(k, out var list))
            {
                list = new List<T>(4);
                _buckets[k] = list;
            }
            list.Add(item);
        }

        /// <summary>Все элементы в ячейке точки + 8 соседних (9 ячеек).</summary>
        public List<T> QueryNear(Point2D p)
        {
            long cx = (long)Math.Floor(p.X / _cell);
            long cy = (long)Math.Floor(p.Y / _cell);
            var result = new List<T>();
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    unchecked
                    {
                        long k = (cx + dx) * 73856093L ^ (cy + dy) * 19349663L;
                        if (_buckets.TryGetValue(k, out var list))
                            result.AddRange(list);
                    }
                }
            }
            return result;
        }

        public int BucketCount => _buckets.Count;
    }
}
