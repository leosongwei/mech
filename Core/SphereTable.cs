// Every hit sphere on the field, built once per tick and shared.
//
// In the Python backend this is a plain tuple returned by
// `systems/projectiles.py:sphere_table` -
// `(centres, radii, ids, modules, factions, blocks, cull, prev, disp)` - because
// numpy arrays carry no names.  Here it is a class, with the same nine pieces
// and the same meaning:
//
//   centres  (n*3) float   world position of each sphere
//   radii    (n)   float   its radius
//   ids      (n)   int     the entity it belongs to (a hull, or a missile)
//   modules  (n)   int     which module a hit on it charges (MOD_*)
//   factions (n)   int     whose side it is on
//   blocks   BlockIndex    CSR from a 128 m block to the spheres that can reach
//                          into it (a round sweeps the blocks its leg crosses)
//   prev     (n*3) float   where each sphere started its own tick
//   disp     (n*3) float   how far it moved during it (the sweep solves
//                          against a moving target, never a standing one)
//   n        int           how many rows are live
//
// The unit block comes first and the missiles after it.  `World.sphere_table`
// builds it once a tick; `projectiles` sweeps it, `blasts` measures against it
// and `proximity` fuses on it.

namespace Mech.Server;

/// <summary>CSR from a block index to the spheres that can reach into it.</summary>
public sealed class BlockIndex
{
    public int[] start = Array.Empty<int>();     // cw*cd + 1
    public int[] items = Array.Empty<int>();     // sphere row numbers
}

public sealed class SphereTable
{
    public float[] centres = Array.Empty<float>();
    public float[] radii = Array.Empty<float>();
    public int[] ids = Array.Empty<int>();
    public int[] modules = Array.Empty<int>();
    public int[] factions = Array.Empty<int>();
    public BlockIndex blocks = new();
    public (float inv, int cw, int cd) cull;
    public float[] prev = Array.Empty<float>();
    public float[] disp = Array.Empty<float>();
    public int n;

    /// <summary>How many of the leading rows belong to hulls (the rest are
    /// missiles) - the Python code's `nu`.</summary>
    public int n_units;
}
