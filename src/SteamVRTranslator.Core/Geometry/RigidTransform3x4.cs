namespace SteamVRTranslator.Core.Geometry;

public readonly record struct RigidTransform3x4(
    float M0,
    float M1,
    float M2,
    float M3,
    float M4,
    float M5,
    float M6,
    float M7,
    float M8,
    float M9,
    float M10,
    float M11)
{
    public Vector3f Translation => new(M3, M7, M11);

    public Vector3f Forward => new(-M2, -M6, -M10);

    public Vector3f InverseRotate(Vector3f vector) =>
        new(
            (M0 * vector.X) + (M4 * vector.Y) + (M8 * vector.Z),
            (M1 * vector.X) + (M5 * vector.Y) + (M9 * vector.Z),
            (M2 * vector.X) + (M6 * vector.Y) + (M10 * vector.Z));

    public Vector3f TransformPoint(Vector3f point) =>
        new(
            (M0 * point.X) + (M1 * point.Y) + (M2 * point.Z) + M3,
            (M4 * point.X) + (M5 * point.Y) + (M6 * point.Z) + M7,
            (M8 * point.X) + (M9 * point.Y) + (M10 * point.Z) + M11);

    public Vector3f InverseTransformPoint(Vector3f point) =>
        InverseRotate(point - Translation);

    public static RigidTransform3x4 Multiply(RigidTransform3x4 left, RigidTransform3x4 right) =>
        new(
            (left.M0 * right.M0) + (left.M1 * right.M4) + (left.M2 * right.M8),
            (left.M0 * right.M1) + (left.M1 * right.M5) + (left.M2 * right.M9),
            (left.M0 * right.M2) + (left.M1 * right.M6) + (left.M2 * right.M10),
            left.TransformPoint(right.Translation).X,
            (left.M4 * right.M0) + (left.M5 * right.M4) + (left.M6 * right.M8),
            (left.M4 * right.M1) + (left.M5 * right.M5) + (left.M6 * right.M9),
            (left.M4 * right.M2) + (left.M5 * right.M6) + (left.M6 * right.M10),
            left.TransformPoint(right.Translation).Y,
            (left.M8 * right.M0) + (left.M9 * right.M4) + (left.M10 * right.M8),
            (left.M8 * right.M1) + (left.M9 * right.M5) + (left.M10 * right.M9),
            (left.M8 * right.M2) + (left.M9 * right.M6) + (left.M10 * right.M10),
            left.TransformPoint(right.Translation).Z);

    public static RigidTransform3x4 Identity => new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0);
}
