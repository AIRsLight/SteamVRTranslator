namespace SteamVRTranslator.Core.Geometry;

public readonly record struct Vector3f(float X, float Y, float Z)
{
    public float Length => MathF.Sqrt(LengthSquared);

    public float LengthSquared => Dot(this, this);

    public static Vector3f operator -(Vector3f left, Vector3f right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Vector3f operator +(Vector3f left, Vector3f right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Vector3f operator *(Vector3f value, float scalar) =>
        new(value.X * scalar, value.Y * scalar, value.Z * scalar);

    public static Vector3f operator /(Vector3f value, float scalar) =>
        new(value.X / scalar, value.Y / scalar, value.Z / scalar);

    public Vector3f Normalized() =>
        Length > 0.000001f ? this / Length : default;

    public static float Dot(Vector3f left, Vector3f right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    public static Vector3f Cross(Vector3f left, Vector3f right) =>
        new(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X));
}
