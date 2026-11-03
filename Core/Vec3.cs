// Small vector / angle helpers.  The Python backend does this arithmetic on
// numpy arrays and plain tuples (doubles); here it is one struct so a system
// can write `p + v * dt` instead of indexing three buffers.
//
// **Everything is `float`.**  The whole simulation is float32 - the component
// buffers, the wire format, the client - so doing the intermediate maths in
// double buys accuracy the storage throws away, costs a conversion on every
// load and store, and would make the port read differently from the file it was
// ported from.  Reach for `double` only where an API forces it (`Math.Sqrt` on
// a `double`, `System.Random.NextDouble`, JSON round-tripping).
//
// The angle helpers (`wrap_pi`, `turn_toward`, ...) live in `Core/MathUtil.cs`
// because they mirror `mathutil.py` one for one.

namespace Mech.Server;

public readonly struct Vec3
{
    public readonly float x, y, z;

    public Vec3(float x, float y, float z)
    {
        this.x = x; this.y = y; this.z = z;
    }

    public static readonly Vec3 zero = new(0f, 0f, 0f);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vec3 operator -(Vec3 a) => new(-a.x, -a.y, -a.z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.x * s, a.y * s, a.z * s);
    public static Vec3 operator *(float s, Vec3 a) => new(a.x * s, a.y * s, a.z * s);
    public static Vec3 operator /(Vec3 a, float s) => new(a.x / s, a.y / s, a.z / s);

    public float dot(Vec3 b) => x * b.x + y * b.y + z * b.z;
    public Vec3 cross(Vec3 b) => new(y * b.z - z * b.y, z * b.x - x * b.z, x * b.y - y * b.x);
    public float len2 => x * x + y * y + z * z;
    public float len => MathF.Sqrt(len2);
    public float horiz => MathF.Sqrt(x * x + z * z);

    public Vec3 unit
    {
        get
        {
            float l = len;
            return l > 1e-12f ? new Vec3(x / l, y / l, z / l) : zero;
        }
    }

    /// <summary>The XZ-plane angle of this vector (the `yaw_of` convention:
    /// yaw 0 lies along +x, and a positive yaw turns towards +z).</summary>
    public float yaw => MathF.Atan2(z, x);

    public float this[int i] => i == 0 ? x : i == 1 ? y : z;

    public static Vec3 of(float[] a) => new(a[0], a[1], a[2]);
    public float[] arr => new[] { x, y, z };
    public (float, float, float) tuple => (x, y, z);

    public override string ToString() => $"({x:0.###}, {y:0.###}, {z:0.###})";
}
