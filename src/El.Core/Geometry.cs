using System;

namespace El.Core
{
    /// <summary>Точка на плоскости (двойная точность, как в AutoCAD).</summary>
    public readonly struct Point2D : IEquatable<Point2D>
    {
        public readonly double X;
        public readonly double Y;

        public Point2D(double x, double y) { X = x; Y = y; }

        public double Dist2(Point2D o)
        {
            double dx = X - o.X, dy = Y - o.Y;
            return dx * dx + dy * dy;
        }

        public double Dist(Point2D o) => Math.Sqrt(Dist2(o));

        public bool Approx(Point2D o, double tol2) => Dist2(o) <= tol2;

        public bool Equals(Point2D other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is Point2D p && Equals(p);
        public override int GetHashCode()
        {
            unchecked
            {
                // квантование не применяем — равенство по точному значению,
                // приближённое сравнение делается через Approx() отдельно
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }

        public override string ToString() => $"({X:F1}, {Y:F1})";
    }

    /// <summary>Отрезок (линия) с уникальным индексом.</summary>
    public sealed class LineSeg
    {
        public int Id { get; }
        public Point2D A { get; }
        public Point2D B { get; }
        public object Tag { get; set; } // ссылка на сущность AutoCAD (ObjectId) — не используется в Core

        public LineSeg(int id, Point2D a, Point2D b)
        {
            Id = id;
            A = a;
            B = b;
        }

        public double Length => A.Dist(B);
        public Point2D Mid => new Point2D((A.X + B.X) / 2.0, (A.Y + B.Y) / 2.0);
    }

    /// <summary>Текстовая подпись с позицией.</summary>
    public sealed class TextLabel
    {
        public Point2D Position { get; }
        public string Text { get; }
        public object Tag { get; set; }

        public TextLabel(Point2D position, string text)
        {
            Position = position;
            Text = text ?? string.Empty;
        }
    }
}
