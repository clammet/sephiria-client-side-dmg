using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    // =====================================================================================
    //  Flow D: host-detected area / ground / boss hits, verified by the client.
    //
    //  Almost every non-projectile hit in the game (boss stomps, cracks, lasers, ground fire,
    //  traps, explosions, chain lightning, ...) is a host-side overlap query
    //  (Physics2D.Overlap*, Collider2D.Overlap, TopdownSpatialHash.*) followed by ApplyDamage
    //  on each result. Those tests run against the host's (lagged) copy of a joined player.
    //
    //  The host hooks the overlap primitives. Whenever a query's results contain a modded
    //  player's hitbox, the exact shape of that query (world geometry, filter, and the transform
    //  it was attached to) is remembered for the current frame. When the effect then calls
    //  ApplyDamage on that player, the shape is attached to the damage query the mod already
    //  sends (see ServerSide flow A). The client re-anchors the shape to its own view of the
    //  source object, tests it against its own hitbox / position at the moment it perceives the
    //  hit, and answers hit / no-hit together with its guard/dodge snapshot.
    // =====================================================================================

    public enum AreaShapeKind : byte { Circle = 0, Box = 1, Capsule = 2, Point = 3, Polygon = 4, Ray = 5 }

    /// <summary>What the host tested the shape against.</summary>
    public enum AreaVictimTest : byte
    {
        HitboxCollider = 0,   // Physics2D overlap against the Hitbox collider(s)
        GroundPoint = 1,      // TopdownSpatialHash: the unit's transform position (a point)
        GroundCircle = 2,     // TopdownSpatialHash.Overlap: the unit's MovementCollider circle
    }

    /// <summary>One host-side hit test: geometry in host world space plus the transform it was attached to.</summary>
    public sealed class AreaShape
    {
        public const int MaxPolygonPoints = 64;
        public const int MaxAnchorDepth = 8;

        public AreaShapeKind kind;
        public AreaVictimTest victim;
        public Vector2 center;        // circle / box / capsule / point centre, ray origin
        public float z;               // height of the centre (TopdownSpatialHash circle tests are 3D)
        public bool circle3D;         // circle test includes the height axis
        public float radius;          // circle radius, ray length
        public Vector2 size;          // box / capsule size, ray direction (normalised)
        public float angle;           // box / capsule rotation in degrees
        public CapsuleDirection2D capsuleDir;
        public readonly List<Vector2> points = new List<Vector2>();   // polygon vertices, world space
        // contact filter used by the host (HitboxCollider / Ray)
        public bool useTriggers;
        public bool useLayerMask;
        public int layerMask;
        public bool rayFirstHitOnly;
        // anchor: the transform the shape was attached to on the host, so the client can move the
        // shape to where it sees that object (interpolated remote objects lag the host)
        public bool hasAnchor;
        public uint anchorNetId;
        public readonly byte[] anchorPath = new byte[MaxAnchorDepth];
        public int anchorDepth;
        public int anchorNameHash;    // name of the anchored child, so the client notices a differing hierarchy
        public Vector2 anchorPos;
        public float anchorAngle;
        public bool anchorFlipX;

        public void Reset()
        {
            kind = AreaShapeKind.Circle; victim = AreaVictimTest.HitboxCollider;
            center = Vector2.zero; z = 0f; circle3D = false; radius = 0f; size = Vector2.zero; angle = 0f;
            capsuleDir = CapsuleDirection2D.Vertical; points.Clear();
            useTriggers = true; useLayerMask = false; layerMask = 0; rayFirstHitOnly = false;
            hasAnchor = false; anchorNetId = 0; anchorDepth = 0; anchorNameHash = 0; anchorPos = Vector2.zero; anchorAngle = 0f; anchorFlipX = false;
        }

        public void CopyFrom(AreaShape o)
        {
            kind = o.kind; victim = o.victim; center = o.center; z = o.z; circle3D = o.circle3D; radius = o.radius; size = o.size; angle = o.angle;
            capsuleDir = o.capsuleDir; points.Clear(); points.AddRange(o.points);
            useTriggers = o.useTriggers; useLayerMask = o.useLayerMask; layerMask = o.layerMask; rayFirstHitOnly = o.rayFirstHitOnly;
            hasAnchor = o.hasAnchor; anchorNetId = o.anchorNetId; anchorDepth = o.anchorDepth; anchorNameHash = o.anchorNameHash;
            Array.Copy(o.anchorPath, anchorPath, MaxAnchorDepth);
            anchorPos = o.anchorPos; anchorAngle = o.anchorAngle; anchorFlipX = o.anchorFlipX;
        }

        public void SetFilter(ContactFilter2D f)
        {
            useTriggers = f.useTriggers;
            useLayerMask = f.useLayerMask;
            layerMask = f.layerMask.value;
        }

        public ContactFilter2D Filter()
        {
            ContactFilter2D f = default(ContactFilter2D);
            f.useTriggers = useTriggers;
            f.useLayerMask = useLayerMask;
            f.layerMask = layerMask;
            f.useDepth = false;
            return f;
        }

        // ---------------------------------------------------------------- wire format

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)kind);
            w.WriteByte((byte)victim);
            w.WriteVector2(center);
            w.WriteFloat(z);
            w.WriteFloat(radius);
            w.WriteVector2(size);
            w.WriteFloat(angle);
            w.WriteByte((byte)capsuleDir);
            byte flags = 0;
            if (useTriggers) flags |= 1;
            if (useLayerMask) flags |= 2;
            if (rayFirstHitOnly) flags |= 4;
            if (hasAnchor) flags |= 8;
            if (circle3D) flags |= 16;
            w.WriteByte(flags);
            w.WriteInt(layerMask);
            if (kind == AreaShapeKind.Polygon)
            {
                int n = Math.Min(points.Count, MaxPolygonPoints);
                w.WriteByte((byte)n);
                for (int i = 0; i < n; i++) w.WriteVector2(points[i]);
            }
            if (hasAnchor)
            {
                w.WriteUInt(anchorNetId);
                w.WriteByte((byte)anchorDepth);
                for (int i = 0; i < anchorDepth; i++) w.WriteByte(anchorPath[i]);
                w.WriteInt(anchorNameHash);
                w.WriteVector2(anchorPos);
                w.WriteFloat(anchorAngle);
                w.WriteBool(anchorFlipX);
            }
        }

        public static AreaShape Read(NetworkReader r)
        {
            AreaShape s = new AreaShape();
            s.kind = (AreaShapeKind)r.ReadByte();
            s.victim = (AreaVictimTest)r.ReadByte();
            s.center = r.ReadVector2();
            s.z = r.ReadFloat();
            s.radius = r.ReadFloat();
            s.size = r.ReadVector2();
            s.angle = r.ReadFloat();
            s.capsuleDir = (CapsuleDirection2D)r.ReadByte();
            byte flags = r.ReadByte();
            s.useTriggers = (flags & 1) != 0;
            s.useLayerMask = (flags & 2) != 0;
            s.rayFirstHitOnly = (flags & 4) != 0;
            s.hasAnchor = (flags & 8) != 0;
            s.circle3D = (flags & 16) != 0;
            s.layerMask = r.ReadInt();
            if (s.kind == AreaShapeKind.Polygon)
            {
                int n = r.ReadByte();
                for (int i = 0; i < n; i++) s.points.Add(r.ReadVector2());
            }
            if (s.hasAnchor)
            {
                s.anchorNetId = r.ReadUInt();
                int depth = r.ReadByte();
                for (int i = 0; i < depth; i++)
                {
                    byte idx = r.ReadByte();
                    if (i < MaxAnchorDepth) s.anchorPath[i] = idx;
                }
                s.anchorDepth = Math.Min(depth, MaxAnchorDepth);
                s.anchorNameHash = r.ReadInt();
                s.anchorPos = r.ReadVector2();
                s.anchorAngle = r.ReadFloat();
                s.anchorFlipX = r.ReadBool();
                if (depth > MaxAnchorDepth) s.hasAnchor = false;
            }
            return s;
        }

        // ---------------------------------------------------------------- transforms

        /// <summary>
        /// Moves the geometry from the anchor's host pose to the pose the client currently sees
        /// (translation, rotation about the anchor, and x-mirroring when the object flipped).
        /// </summary>
        public void Rebase(Vector2 clientPos, float clientAngle, bool clientFlipX)
        {
            bool flip = clientFlipX != anchorFlipX;
            center = Map(center, clientPos, clientAngle, flip);
            switch (kind)
            {
                case AreaShapeKind.Box:
                case AreaShapeKind.Capsule:
                    angle = MapAngle(angle, clientAngle, flip);
                    break;
                case AreaShapeKind.Ray:
                    size = MapDir(size, clientAngle, flip);
                    break;
                case AreaShapeKind.Polygon:
                    for (int i = 0; i < points.Count; i++) points[i] = Map(points[i], clientPos, clientAngle, flip);
                    break;
            }
        }

        private Vector2 Map(Vector2 p, Vector2 clientPos, float clientAngle, bool flip)
        {
            Vector2 d = AreaGeom.Rotate(p - anchorPos, -anchorAngle);
            if (flip) d.x = -d.x;
            return clientPos + AreaGeom.Rotate(d, clientAngle);
        }

        private Vector2 MapDir(Vector2 v, float clientAngle, bool flip)
        {
            v = AreaGeom.Rotate(v, -anchorAngle);
            if (flip) v.x = -v.x;
            return AreaGeom.Rotate(v, clientAngle);
        }

        private float MapAngle(float a, float clientAngle, bool flip)
        {
            float local = a - anchorAngle;
            if (flip) local = 180f - local;
            return local + clientAngle;
        }

        /// <summary>A copy grown by <paramref name="margin"/> world units on every side; null when the kind cannot be grown.</summary>
        public AreaShape Expanded(float margin)
        {
            AreaShape e = new AreaShape();
            e.CopyFrom(this);
            switch (kind)
            {
                case AreaShapeKind.Circle: e.radius += margin; break;
                case AreaShapeKind.Box: e.size += new Vector2(2f * margin, 2f * margin); break;
                case AreaShapeKind.Capsule: e.size += new Vector2(2f * margin, 2f * margin); break;
                case AreaShapeKind.Point: e.kind = AreaShapeKind.Circle; e.radius = margin; break;
                default: return null;
            }
            return e;
        }

        public override string ToString()
        {
            string g;
            switch (kind)
            {
                case AreaShapeKind.Circle: g = "circle " + center + " r=" + radius.ToString("0.00"); break;
                case AreaShapeKind.Box: g = "box " + center + " " + size + " a=" + angle.ToString("0"); break;
                case AreaShapeKind.Capsule: g = "capsule " + center + " " + size + " a=" + angle.ToString("0"); break;
                case AreaShapeKind.Point: g = "point " + center; break;
                case AreaShapeKind.Polygon: g = "polygon[" + points.Count + "]"; break;
                case AreaShapeKind.Ray: g = "ray " + center + " dir=" + size + " len=" + radius.ToString("0.0"); break;
                default: g = kind.ToString(); break;
            }
            return g + " vs " + victim + (hasAnchor ? " @" + anchorNetId : "");
        }
    }

    /// <summary>Pure geometry helpers shared by the host (widening) and the client (verification).</summary>
    public static class AreaGeom
    {
        public static Vector2 Rotate(Vector2 v, float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            float c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        public static float DistPointSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-8f) return (p - a).magnitude;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return (p - (a + ab * t)).magnitude;
        }

        public static bool PointInPolygon(Vector2 p, List<Vector2> pts)
        {
            int n = pts.Count;
            if (n < 3) return false;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = pts[i], b = pts[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        public static float PolygonEdgeDistance(Vector2 p, List<Vector2> pts)
        {
            int n = pts.Count;
            float best = float.MaxValue;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float d = DistPointSegment(p, pts[j], pts[i]);
                if (d < best) best = d;
            }
            return best;
        }

        public static float BoxDistance(Vector2 c, Vector2 size, float angle, Vector2 p)
        {
            Vector2 l = Rotate(p - c, -angle);
            float dx = Mathf.Max(Mathf.Abs(l.x) - size.x * 0.5f, 0f);
            float dy = Mathf.Max(Mathf.Abs(l.y) - size.y * 0.5f, 0f);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        public static void CapsuleSegment(AreaShape s, out Vector2 a, out Vector2 b, out float r)
        {
            bool vertical = s.capsuleDir == CapsuleDirection2D.Vertical;
            float w = vertical ? s.size.x : s.size.y;
            float h = vertical ? s.size.y : s.size.x;
            r = w * 0.5f;
            float half = Mathf.Max(0f, h * 0.5f - r);
            Vector2 axis = Rotate(vertical ? Vector2.up : Vector2.right, s.angle);
            a = s.center - axis * half;
            b = s.center + axis * half;
        }

        private static Vector2 RayEnd(AreaShape s)
        {
            float len = float.IsInfinity(s.radius) || s.radius > 1000f ? 1000f : s.radius;
            return s.center + s.size * len;
        }

        /// <summary>GroundPoint semantics: is the unit's position inside the shape.</summary>
        public static bool ContainsPoint(AreaShape s, Vector3 p)
        {
            switch (s.kind)
            {
                case AreaShapeKind.Circle:
                {
                    float dx = p.x - s.center.x, dy = p.y - s.center.y;
                    float dz = s.circle3D ? p.z - s.z : 0f;
                    return dx * dx + dy * dy + dz * dz <= s.radius * s.radius;
                }
                case AreaShapeKind.Box:
                {
                    Vector2 l = Rotate((Vector2)p - s.center, -s.angle);
                    return Mathf.Abs(l.x) <= s.size.x * 0.5f && Mathf.Abs(l.y) <= s.size.y * 0.5f;
                }
                case AreaShapeKind.Capsule:
                {
                    Vector2 a, b; float r;
                    CapsuleSegment(s, out a, out b, out r);
                    return DistPointSegment(p, a, b) <= r;
                }
                case AreaShapeKind.Polygon: return PointInPolygon(p, s.points);
                case AreaShapeKind.Point: return ((Vector2)p - s.center).sqrMagnitude <= 0.0001f;
                case AreaShapeKind.Ray: return DistPointSegment(p, s.center, RayEnd(s)) <= 0.01f;
            }
            return false;
        }

        /// <summary>GroundCircle semantics: does a circle (the unit's MovementCollider) touch the shape.</summary>
        public static bool OverlapsCircle(AreaShape s, Vector2 c, float R)
        {
            switch (s.kind)
            {
                case AreaShapeKind.Circle: { float rr = s.radius + R; return (c - s.center).sqrMagnitude <= rr * rr; }
                case AreaShapeKind.Box: return BoxDistance(s.center, s.size, s.angle, c) <= R;
                case AreaShapeKind.Capsule:
                {
                    Vector2 a, b; float r;
                    CapsuleSegment(s, out a, out b, out r);
                    return DistPointSegment(c, a, b) <= r + R;
                }
                case AreaShapeKind.Polygon: return PointInPolygon(c, s.points) || PolygonEdgeDistance(c, s.points) <= R;
                case AreaShapeKind.Point: return (c - s.center).sqrMagnitude <= R * R;
                case AreaShapeKind.Ray: return DistPointSegment(c, s.center, RayEnd(s)) <= R;
            }
            return false;
        }

        private static readonly List<Collider2D> _scratch = new List<Collider2D>(64);
        private static readonly List<RaycastHit2D> _rayScratch = new List<RaycastHit2D>(64);

        /// <summary>
        /// HitboxCollider semantics: runs the same kind of Physics2D query the host ran and asks
        /// whether any of <paramref name="mine"/> is among the results. Polygons (which Unity has no
        /// direct query for) are tested geometrically against the colliders.
        /// </summary>
        public static bool HitboxOverlaps(AreaShape s, Collider2D[] mine)
        {
            if (mine == null || mine.Length == 0) return false;
            ContactFilter2D f = s.Filter();
            _scratch.Clear();
            switch (s.kind)
            {
                case AreaShapeKind.Circle:
                    Physics2D.OverlapCircle(s.center, s.radius, f, _scratch);
                    return ContainsAny(_scratch, mine);
                case AreaShapeKind.Box:
                    Physics2D.OverlapBox(s.center, s.size, s.angle, f, _scratch);
                    return ContainsAny(_scratch, mine);
                case AreaShapeKind.Capsule:
                    Physics2D.OverlapCapsule(s.center, s.size, s.capsuleDir, s.angle, f, _scratch);
                    return ContainsAny(_scratch, mine);
                case AreaShapeKind.Point:
                    Physics2D.OverlapPoint(s.center, f, _scratch);
                    return ContainsAny(_scratch, mine);
                case AreaShapeKind.Polygon:
                    for (int i = 0; i < mine.Length; i++)
                        if (mine[i] != null && PolygonOverlapsCollider(s.points, mine[i])) return true;
                    return false;
                case AreaShapeKind.Ray:
                {
                    _rayScratch.Clear();
                    int n = Physics2D.Raycast(s.center, s.size, f, _rayScratch, s.radius);
                    if (n <= 0 || _rayScratch.Count == 0) return false;
                    if (!s.rayFirstHitOnly)
                    {
                        for (int i = 0; i < _rayScratch.Count; i++)
                            if (IsMine(_rayScratch[i].collider, mine)) return true;
                        return false;
                    }
                    int best = 0;
                    for (int i = 1; i < _rayScratch.Count; i++)
                        if (_rayScratch[i].distance < _rayScratch[best].distance) best = i;
                    return IsMine(_rayScratch[best].collider, mine);
                }
            }
            return false;
        }

        public static bool IsMine(Collider2D c, Collider2D[] mine)
        {
            if (c == null || mine == null) return false;
            for (int i = 0; i < mine.Length; i++) if (ReferenceEquals(mine[i], c)) return true;
            return false;
        }

        /// <summary>World centre and radius of a unit's MovementCollider (what TopdownSpatialHash.Overlap tests).</summary>
        public static void MovementCircle(CircleCollider2D mc, out Vector2 center, out float radius)
        {
            Vector3 ls = mc.transform.lossyScale;
            center = mc.transform.TransformPoint(mc.offset);
            radius = mc.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y));
        }

        private static bool ContainsAny(List<Collider2D> results, Collider2D[] mine)
        {
            for (int i = 0; i < results.Count; i++) if (IsMine(results[i], mine)) return true;
            return false;
        }

        /// <summary>Approximate polygon-vs-collider test: polygon contains the collider centre, or a point of the polygon outline lies inside the collider.</summary>
        public static bool PolygonOverlapsCollider(List<Vector2> pts, Collider2D col)
        {
            if (pts.Count < 3 || col == null) return false;
            if (PointInPolygon(col.bounds.center, pts)) return true;
            const float step = 0.2f;
            int n = pts.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = pts[j], b = pts[i];
                if (col.OverlapPoint(a)) return true;
                float len = (b - a).magnitude;
                int k = Mathf.CeilToInt(len / step);
                for (int q = 1; q < k; q++)
                    if (col.OverlapPoint(Vector2.Lerp(a, b, (float)q / k))) return true;
            }
            return false;
        }

        /// <summary>The Hitbox colliders of a unit that host-side overlap tests can find.</summary>
        public static Collider2D[] HitboxColliders(CombatBehaviour u)
        {
            List<Collider2D> list = new List<Collider2D>();
            if (u == null) return list.ToArray();
            Hitbox[] boxes = u.GetComponentsInChildren<Hitbox>(true);
            for (int i = 0; i < boxes.Length; i++)
            {
                Hitbox hb = boxes[i];
                if (hb == null || hb.combatBehaviour != u || !hb.gameObject.activeInHierarchy) continue;
                Collider2D[] cs = hb.GetComponents<Collider2D>();
                for (int j = 0; j < cs.Length; j++)
                    if (cs[j] != null && cs[j].enabled) list.Add(cs[j]);
            }
            return list.ToArray();
        }

        /// <summary>Fills <paramref name="s"/> with the world geometry of a collider (the shape of a Collider2D.Overlap query).</summary>
        public static void FromCollider(AreaShape s, Collider2D c)
        {
            Transform t = c.transform;
            Vector3 ls = t.lossyScale;
            Vector2 sc = new Vector2(Mathf.Abs(ls.x), Mathf.Abs(ls.y));
            s.z = t.position.z;
            CircleCollider2D cc = c as CircleCollider2D;
            if (cc != null)
            {
                s.kind = AreaShapeKind.Circle;
                s.center = t.TransformPoint(cc.offset);
                s.radius = cc.radius * Mathf.Max(sc.x, sc.y);
                return;
            }
            BoxCollider2D bc = c as BoxCollider2D;
            if (bc != null)
            {
                s.kind = AreaShapeKind.Box;
                s.center = t.TransformPoint(bc.offset);
                s.size = Vector2.Scale(bc.size, sc) + new Vector2(2f * bc.edgeRadius, 2f * bc.edgeRadius);
                s.angle = t.eulerAngles.z;
                return;
            }
            CapsuleCollider2D cap = c as CapsuleCollider2D;
            if (cap != null)
            {
                s.kind = AreaShapeKind.Capsule;
                s.center = t.TransformPoint(cap.offset);
                s.size = Vector2.Scale(cap.size, sc);
                s.capsuleDir = cap.direction;
                s.angle = t.eulerAngles.z;
                return;
            }
            PolygonCollider2D pc = c as PolygonCollider2D;
            if (pc != null && pc.pathCount == 1)
            {
                Vector2[] path = pc.GetPath(0);
                if (path != null && path.Length >= 3 && path.Length <= AreaShape.MaxPolygonPoints)
                {
                    s.kind = AreaShapeKind.Polygon;
                    s.points.Clear();
                    for (int i = 0; i < path.Length; i++) s.points.Add(t.TransformPoint(path[i] + pc.offset));
                    Vector2 sum = Vector2.zero;
                    for (int i = 0; i < s.points.Count; i++) sum += s.points[i];
                    s.center = sum / s.points.Count;
                    return;
                }
            }
            // anything else: its axis aligned bounds
            Bounds b = c.bounds;
            s.kind = AreaShapeKind.Box;
            s.center = b.center;
            s.size = b.size;
            s.angle = 0f;
        }
    }

    // =====================================================================================
    //  Host side
    // =====================================================================================

    /// <summary>
    /// Tracks which game object is currently executing a hit test. Effect methods discovered at
    /// runtime (see AreaRecorder.Discover) get a prefix/finalizer pair that pushes/pops their
    /// instance here, so a recorded overlap can be (a) anchored to the object that ran it and
    /// (b) matched to the ApplyDamage call that follows it.
    /// </summary>
    internal static class CallerContext
    {
        private struct Frame
        {
            public object instance;
            public MethodBase method;
        }

        private static readonly List<Frame> _stack = new List<Frame>(8);
        private static readonly Dictionary<Type, FieldInfo> _thisFields = new Dictionary<Type, FieldInfo>();
        private static int _frame = -1;

        /// <summary>The game object (component) whose method is currently running, or null.</summary>
        public static object Current
        {
            get { return _stack.Count > 0 && _frame == Time.frameCount ? _stack[_stack.Count - 1].instance : null; }
        }

        /// <summary>The tracked method currently running, or null.</summary>
        public static MethodBase CurrentMethod
        {
            get { return _stack.Count > 0 && _frame == Time.frameCount ? _stack[_stack.Count - 1].method : null; }
        }

        public static void Prefix(object __instance, MethodBase __originalMethod)
        {
            int f = Time.frameCount;
            if (f != _frame) { _stack.Clear(); _frame = f; }   // safety net: never carry entries across frames
            _stack.Add(new Frame { instance = Resolve(__instance), method = __originalMethod });
        }

        public static void Finalizer()
        {
            if (_stack.Count > 0 && _frame == Time.frameCount) _stack.RemoveAt(_stack.Count - 1);
        }

        /// <summary>Compiler generated coroutine / closure objects carry the real instance in "&lt;&gt;4__this".</summary>
        private static object Resolve(object inst)
        {
            if (inst == null || inst is Component) return inst;
            Type t = inst.GetType();
            FieldInfo fi;
            if (!_thisFields.TryGetValue(t, out fi))
            {
                fi = t.GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _thisFields[t] = fi;
            }
            if (fi != null)
            {
                object outer = fi.GetValue(inst);
                if (outer != null) return outer;
            }
            return inst;
        }
    }

    internal static class AreaRecorder
    {
        private sealed class Target
        {
            public PlayerAvatar pa;
            public Collider2D[] hitboxes;
            public TopdownRigidbody rb;
        }

        private sealed class Record
        {
            public int frame = -1;
            public PlayerAvatar victim;
            public object ctx;
            public MethodBase method;
            public bool widened;      // the host's real test missed; the player was injected for the client to decide
            public bool consumed;     // already handed to an ApplyDamage
            public readonly AreaShape shape = new AreaShape();
        }

        private const int RingSize = 32;
        private static readonly List<Target> _targets = new List<Target>();
        private static readonly Record[] _ring = new Record[RingSize];
        private static int _ringNext;
        private static readonly AreaShape _tmp = new AreaShape();
        private static readonly byte[] _pathTmp = new byte[AreaShape.MaxAnchorDepth];
        private static readonly Assembly _gameAssembly = typeof(UnitAvatar).Assembly;
        private static readonly Dictionary<NetworkIdentity, bool> _movesOnClients = new Dictionary<NetworkIdentity, bool>();

        // discovery of effect methods
        private static readonly HashSet<MethodBase> _seen = new HashSet<MethodBase>();
        private static readonly Queue<MethodBase> _toPatch = new Queue<MethodBase>();
        /// <summary>Tracked methods whose hit tests were followed by an ApplyDamage on a found player (i.e. damage tests, not perception / targeting queries).</summary>
        private static readonly HashSet<MethodBase> _damageMethods = new HashSet<MethodBase>();
        private static float _nextDiscover;
        private static int _hookErrors;

        /// <summary>True while a modded client with the AreaHits feature is connected (host only). Checked first thing in every hook.</summary>
        public static bool Armed;
        /// <summary>Set while our own physics queries run (widening tests) so the hooks ignore them.</summary>
        public static bool Suppress;

        static AreaRecorder()
        {
            for (int i = 0; i < RingSize; i++) _ring[i] = new Record();
        }

        // ------------------------------------------------------------------ lifecycle

        public static void Clear()
        {
            _targets.Clear();
            _movesOnClients.Clear();
            Armed = false;
            for (int i = 0; i < RingSize; i++) { _ring[i].frame = -1; _ring[i].victim = null; _ring[i].ctx = null; _ring[i].method = null; _ring[i].consumed = false; }
        }

        /// <summary>Rebuilds the list of players whose hits are client-verified (their hitbox colliders may change; called periodically).</summary>
        public static void Refresh(List<PlayerAvatar> avatars)
        {
            _targets.Clear();
            _movesOnClients.Clear();
            if (avatars != null && Plugin.On && Plugin.HostAreaHitAuthority.Value)
            {
                for (int i = 0; i < avatars.Count; i++)
                {
                    PlayerAvatar pa = avatars[i];
                    if (pa == null) continue;
                    Target t = new Target();
                    t.pa = pa;
                    t.hitboxes = AreaGeom.HitboxColliders(pa);
                    t.rb = pa.TopdownRigidbody;
                    _targets.Add(t);
                }
            }
            Armed = _targets.Count > 0;
        }

        public static void Tick()
        {
            if (_toPatch.Count == 0) return;
            MethodBase m = _toPatch.Dequeue();
            PatchCaller(m, false);
        }

        /// <summary>Methods we know run hit tests and that are already patched by the mod (so a stack walk would not find them).</summary>
        public static void PatchKnownCallers()
        {
            MethodBase[] known =
            {
                AccessTools.Method(typeof(Bullet), "Update"),
                AccessTools.Method(typeof(MeleeCollision), "Update"),
            };
            for (int i = 0; i < known.Length; i++)
            {
                if (known[i] == null) continue;
                _seen.Add(known[i]);
                PatchCaller(known[i], true);
            }
        }

        private static readonly HarmonyMethod _ctxPrefix = new HarmonyMethod(AccessTools.Method(typeof(CallerContext), "Prefix"));
        private static readonly HarmonyMethod _ctxFinalizer = new HarmonyMethod(AccessTools.Method(typeof(CallerContext), "Finalizer"));

        private static void PatchCaller(MethodBase m, bool quiet)
        {
            try
            {
                Plugin.HarmonyInstance.Patch(m, _ctxPrefix, null, null, _ctxFinalizer, null);
                if (!quiet) Plugin.Log.LogInfo("[CSD/host] area hits: tracking " + Describe(m));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[CSD/host] area hits: could not track " + Describe(m) + " (shapes from it stay in world space): " + e.Message);
            }
        }

        private static string Describe(MethodBase m)
        {
            Type t = m.DeclaringType;
            string owner = t == null ? "?" : (t.DeclaringType != null ? t.DeclaringType.Name + "." + t.Name : t.Name);
            return owner + "." + m.Name;
        }

        /// <summary>
        /// A hit test involving a modded player ran without a known caller: find the game method
        /// on the stack that ran it and queue it for context tracking. Rate limited; walks the
        /// stack only for tests that actually involve a modded player.
        /// </summary>
        private static void Discover()
        {
            if (Time.unscaledTime < _nextDiscover) return;
            _nextDiscover = Time.unscaledTime + 0.25f;
            try
            {
                StackTrace st = new StackTrace(1, false);
                int frames = Math.Min(st.FrameCount, 40);
                for (int i = 0; i < frames; i++)
                {
                    StackFrame f = st.GetFrame(i);
                    MethodBase m = f == null ? null : f.GetMethod();
                    if (m == null) continue;
                    Type t = m.DeclaringType;
                    if (t == null || t.Assembly != _gameAssembly) continue;
                    if (t == typeof(HorayPhysics2D) || t == typeof(TopdownSpatialHash)) continue;
                    if (m.IsStatic || m is ConstructorInfo || m.IsAbstract || m.ContainsGenericParameters || t.ContainsGenericParameters || t.IsValueType) continue;
                    if (_seen.Add(m)) _toPatch.Enqueue(m);
                    return;
                }
            }
            catch (Exception e)
            {
                if (_hookErrors++ < 5) Plugin.Log.LogWarning("[CSD/host] area hits: caller discovery failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ hook entry points

        public static void LogHookError(Exception e)
        {
            if (_hookErrors++ < 5) Plugin.Log.LogError("[CSD/host] area hit hook failed: " + e);
        }

        /// <summary>
        /// Widening injects a player into a query's results although the host's test missed. That
        /// is only sound for queries that exist to deal damage (the injected hit is then verified
        /// by the client through ApplyDamage); a perception / targeting / buff query would apply
        /// its effect unverified. So it is limited to tracked methods that have already been seen
        /// to follow a hit test with an ApplyDamage on the found player, and never to projectiles /
        /// swings (those consume themselves on a hit).
        /// </summary>
        private static bool WideningAllowed(object ctx, MethodBase method)
        {
            if (Plugin.HostAreaHitMargin.Value <= 0f) return false;
            if (ctx == null || method == null || !_damageMethods.Contains(method)) return false;
            return !(ctx is Bullet) && !(ctx is MeleeCollision) && !(ctx is BulletMoveModule) && !(ctx is BulletDestroyModule);
        }

        /// <summary>Would this filter have let the collider through? (Physics2D semantics: layer mask, trigger flag, depth.)</summary>
        private static bool PassesFilter(ContactFilter2D f, Collider2D c)
        {
            return !f.IsFilteringLayerMask(c.gameObject) && !f.IsFilteringTrigger(c) && !f.IsFilteringDepth(c.gameObject);
        }

        /// <summary>Physics2D / Collider2D overlap query finished. <paramref name="source"/> is set for Collider2D.Overlap.</summary>
        public static void OnPhysicsQuery(AreaShapeKind kind, Vector2 center, float radius, Vector2 size, float angle, CapsuleDirection2D capDir,
            ContactFilter2D filter, Collider2D[] arr, List<Collider2D> list, ref int count, Collider2D source)
        {
            if (!Armed || Suppress) return;
            object ctx = CallerContext.Current;
            MethodBase method = CallerContext.CurrentMethod;
            int n = count;
            if (arr != null) n = Mathf.Min(n, arr.Length);
            else if (list != null) n = Mathf.Min(n, list.Count);
            else n = 0;
            bool recorded = false;
            for (int t = 0; t < _targets.Count; t++)
            {
                Target tg = _targets[t];
                if (tg.pa == null || tg.hitboxes == null || tg.hitboxes.Length == 0) continue;
                bool found = false;
                for (int i = 0; i < n && !found; i++)
                {
                    Collider2D c = arr != null ? arr[i] : list[i];
                    if (c != null && AreaGeom.IsMine(c, tg.hitboxes)) found = true;
                }
                if (found)
                {
                    Record r = NewRecord(tg.pa, ctx, method, false);
                    FillPhysics(r.shape, kind, center, radius, size, angle, capDir, filter, source, ctx);
                    recorded = true;
                    continue;
                }
                if (!WideningAllowed(ctx, method)) continue;
                // an effect centred on the player itself (its own collider is the query) must not find its owner
                if (source != null && source.transform.IsChildOf(tg.pa.transform)) continue;
                // the host narrowly missed: let the client decide with the exact shape
                FillPhysics(_tmp, kind, center, radius, size, angle, capDir, filter, source, ctx);
                Collider2D hb = null;
                Suppress = true;
                try
                {
                    float m = Plugin.HostAreaHitMargin.Value;
                    if (source != null)
                    {
                        // Collider2D.Overlap: distance from the source collider, honouring the query's filter
                        for (int h = 0; h < tg.hitboxes.Length && hb == null; h++)
                        {
                            Collider2D cand = tg.hitboxes[h];
                            if (cand == null || !PassesFilter(filter, cand)) continue;
                            ColliderDistance2D d = source.Distance(cand);
                            if (d.isValid && d.distance <= m) hb = cand;
                        }
                    }
                    else
                    {
                        AreaShape e = _tmp.Expanded(m);
                        if (e != null && AreaGeom.HitboxOverlaps(e, tg.hitboxes))
                        {
                            for (int h = 0; h < tg.hitboxes.Length && hb == null; h++)
                                if (tg.hitboxes[h] != null && PassesFilter(filter, tg.hitboxes[h])) hb = tg.hitboxes[h];
                        }
                    }
                }
                finally { Suppress = false; }
                if (hb == null) continue;
                if (arr != null) { if (count >= arr.Length) continue; arr[count] = hb; count++; }
                else if (list != null) { list.Add(hb); count++; }
                else continue;
                Record wr = NewRecord(tg.pa, ctx, method, true);
                wr.shape.CopyFrom(_tmp);
                recorded = true;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] area test widened for " + tg.pa.name + ": " + wr.shape);
            }
            if (recorded && ctx == null) Discover();
        }

        /// <summary>TopdownSpatialHash query finished (results are TopdownRigidbody references).</summary>
        public static void OnGroundQuery(AreaShapeKind kind, Vector3 center, float radius, Vector2 size, float angle,
            TopdownRigidbody[] hits, ref int count, Collider2D source, AreaVictimTest victimTest, bool circle3D)
        {
            if (!Armed || Suppress || hits == null) return;
            object ctx = CallerContext.Current;
            MethodBase method = CallerContext.CurrentMethod;
            int n = Mathf.Min(count, hits.Length);
            bool recorded = false;
            for (int t = 0; t < _targets.Count; t++)
            {
                Target tg = _targets[t];
                if (tg.pa == null || tg.rb == null) continue;
                bool found = false;
                for (int i = 0; i < n; i++)
                    if (ReferenceEquals(hits[i], tg.rb)) { found = true; break; }
                if (found)
                {
                    Record r = NewRecord(tg.pa, ctx, method, false);
                    FillGround(r.shape, kind, center, radius, size, angle, source, victimTest, circle3D, ctx);
                    recorded = true;
                    continue;
                }
                if (!WideningAllowed(ctx, method) || count >= hits.Length) continue;
                if (source != null && source.transform.IsChildOf(tg.pa.transform)) continue;
                FillGround(_tmp, kind, center, radius, size, angle, source, victimTest, circle3D, ctx);
                AreaShape e = _tmp.Expanded(Plugin.HostAreaHitMargin.Value);
                if (e == null) continue;
                bool near;
                if (victimTest == AreaVictimTest.GroundCircle && tg.rb.MovementCollider != null)
                {
                    Vector2 c; float r;
                    AreaGeom.MovementCircle(tg.rb.MovementCollider, out c, out r);
                    near = AreaGeom.OverlapsCircle(e, c, r);
                }
                else
                {
                    near = AreaGeom.ContainsPoint(e, tg.rb.transform.position);
                }
                if (!near) continue;
                hits[count] = tg.rb;
                count++;
                Record wr = NewRecord(tg.pa, ctx, method, true);
                wr.shape.CopyFrom(_tmp);
                recorded = true;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] ground test widened for " + tg.pa.name + ": " + wr.shape);
            }
            if (recorded && ctx == null) Discover();
        }

        /// <summary>Single-hit raycast finished.</summary>
        public static void OnRaycast(Vector2 origin, Vector2 direction, float distance, ContactFilter2D filter, RaycastHit2D hit)
        {
            if (!Armed || Suppress) return;
            Collider2D c = hit.collider;
            if (c == null) return;
            for (int t = 0; t < _targets.Count; t++)
            {
                Target tg = _targets[t];
                if (tg.pa == null || tg.hitboxes == null) continue;
                if (!AreaGeom.IsMine(c, tg.hitboxes)) continue;
                object ctx = CallerContext.Current;
                Record r = NewRecord(tg.pa, ctx, CallerContext.CurrentMethod, false);
                AreaShape s = r.shape;
                s.Reset();
                s.kind = AreaShapeKind.Ray;
                s.victim = AreaVictimTest.HitboxCollider;
                s.center = origin;
                s.size = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector2.right;
                s.radius = distance;
                s.rayFirstHitOnly = true;
                s.SetFilter(filter);
                SetAnchor(s, ctx as Component, false);
                if (ctx == null) Discover();
            }
        }

        // ------------------------------------------------------------------ records

        private static Record NewRecord(PlayerAvatar victim, object ctx, MethodBase method, bool widened)
        {
            Record r = _ring[_ringNext];
            _ringNext = (_ringNext + 1) % RingSize;
            r.frame = Time.frameCount;
            r.victim = victim;
            r.ctx = ctx;
            r.method = method;
            r.widened = widened;
            r.consumed = false;
            r.shape.Reset();
            return r;
        }

        private static void FillPhysics(AreaShape s, AreaShapeKind kind, Vector2 center, float radius, Vector2 size, float angle, CapsuleDirection2D capDir,
            ContactFilter2D filter, Collider2D source, object ctx)
        {
            s.Reset();
            s.victim = AreaVictimTest.HitboxCollider;
            s.SetFilter(filter);
            if (source != null)
            {
                AreaGeom.FromCollider(s, source);
                SetAnchor(s, source, true);   // the collider IS the shape: it moves with its transform by definition
                return;
            }
            s.kind = kind;
            s.center = center;
            s.radius = radius;
            s.size = size;
            s.angle = angle;
            s.capsuleDir = capDir;
            SetAnchor(s, ctx as Component, false);
        }

        private static void FillGround(AreaShape s, AreaShapeKind kind, Vector3 center, float radius, Vector2 size, float angle,
            Collider2D source, AreaVictimTest victimTest, bool circle3D, object ctx)
        {
            s.Reset();
            s.victim = victimTest;
            if (source != null)
            {
                AreaGeom.FromCollider(s, source);
                SetAnchor(s, source, true);
                return;
            }
            s.kind = kind;
            s.center = center;
            s.z = center.z;
            s.circle3D = circle3D;
            s.radius = radius;
            s.size = size;
            s.angle = angle;
            SetAnchor(s, ctx as Component, false);
        }

        /// <summary>
        /// A world-space query (no source collider) is only assumed to move with the object that
        /// ran it when it is centred close to that object: a stomp around a boss, a laser slab in
        /// front of it. A telegraphed circle at the player's position or a crack the boss left
        /// behind while moving on is world-fixed; re-anchoring it would shift (and on a facing
        /// flip, mirror) it by the caller's client-side lag.
        /// </summary>
        private const float AttachSlack = 2f;

        private static bool LooksAttached(AreaShape s, Transform t)
        {
            float extent;
            switch (s.kind)
            {
                case AreaShapeKind.Circle: extent = s.radius; break;
                case AreaShapeKind.Box:
                case AreaShapeKind.Capsule: extent = s.size.magnitude * 0.5f; break;
                case AreaShapeKind.Ray: extent = float.IsInfinity(s.radius) ? 1000f : s.radius; break;
                case AreaShapeKind.Polygon:
                {
                    extent = 0f;
                    for (int i = 0; i < s.points.Count; i++) extent = Mathf.Max(extent, (s.points[i] - s.center).magnitude);
                    break;
                }
                default: extent = 0f; break;
            }
            return (s.center - (Vector2)t.position).magnitude <= extent + AttachSlack;
        }

        /// <summary>Only objects the client moves between spawn messages can be seen at a different pose than the host has.</summary>
        private static bool MovesOnClients(NetworkIdentity ni)
        {
            bool moves;
            if (!_movesOnClients.TryGetValue(ni, out moves))
            {
                moves = ni.GetComponentInChildren<NetworkTransformBase>(true) != null;
                _movesOnClients[ni] = moves;
            }
            return moves;
        }

        /// <summary>
        /// Remembers which networked object (and which child of it) the shape belongs to, and that
        /// object's pose right now - if the shape plausibly moves with it.
        /// </summary>
        private static void SetAnchor(AreaShape s, Component c, bool attached)
        {
            s.hasAnchor = false;
            if (c == null) return;
            Transform t = c.transform;
            NetworkIdentity ni = t.GetComponentInParent<NetworkIdentity>();
            if (ni == null || ni.netId == 0) return;
            if (!MovesOnClients(ni)) return;
            if (!attached && !LooksAttached(s, t)) return;
            int depth = 0;
            Transform cur = t;
            while (cur != ni.transform)
            {
                if (depth >= AreaShape.MaxAnchorDepth || cur == null) return;
                int idx = cur.GetSiblingIndex();
                if (idx > 255) return;
                _pathTmp[depth++] = (byte)idx;
                cur = cur.parent;
                if (cur == null) return;
            }
            for (int i = 0; i < depth; i++) s.anchorPath[i] = _pathTmp[depth - 1 - i];
            s.anchorDepth = depth;
            s.anchorNetId = ni.netId;
            s.anchorNameHash = t.name.GetStableHashCode();
            s.anchorPos = t.position;
            s.anchorAngle = t.eulerAngles.z;
            s.anchorFlipX = t.lossyScale.x < 0f;
            s.hasAnchor = true;
        }

        /// <summary>
        /// The shape of the hit test this frame that found <paramref name="victim"/> and ran under
        /// the same caller context as the ApplyDamage we are handling, or null. Records from
        /// untracked callers (ctx == null) are never handed out: without a caller identity a
        /// perception query of one object could be attached to the damage of another. Among a
        /// caller's records the most recent one from a method already known to deal damage wins
        /// over e.g. a sight-radius query it ran in between. Each record is handed out once.
        /// <paramref name="widened"/> is set when the host's real test missed and the hit only
        /// exists if the client confirms it.
        /// </summary>
        public static AreaShape FindShape(PlayerAvatar victim, object ctx, out bool widened)
        {
            widened = false;
            if (!Armed || victim == null || ctx == null) return null;
            int frame = Time.frameCount;
            Record best = null;
            for (int k = 1; k <= RingSize; k++)
            {
                Record r = _ring[(_ringNext - k + RingSize) % RingSize];
                if (r.frame != frame) continue;
                if (r.consumed || r.victim != victim) continue;
                if (!ReferenceEquals(r.ctx, ctx)) continue;
                if (best == null) best = r;
                if (r.method != null && _damageMethods.Contains(r.method)) { best = r; break; }
            }
            if (best == null) return null;
            best.consumed = true;
            if (best.method != null) _damageMethods.Add(best.method);
            widened = best.widened;
            return best.shape;
        }
    }

    // ------------------------------------------------------------------ hooks on the overlap primitives

    [HarmonyPatch(typeof(Physics2D), nameof(Physics2D.OverlapCircle), new Type[] { typeof(Vector2), typeof(float), typeof(ContactFilter2D), typeof(Collider2D[]) })]
    internal static class AreaHook_Physics2D_OverlapCircle
    {
        private static void Postfix(Vector2 __0, float __1, ContactFilter2D __2, Collider2D[] __3, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnPhysicsQuery(AreaShapeKind.Circle, __0, __1, Vector2.zero, 0f, CapsuleDirection2D.Vertical, __2, __3, null, ref __result, null); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(Physics2D), nameof(Physics2D.OverlapBox), new Type[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(ContactFilter2D), typeof(Collider2D[]) })]
    internal static class AreaHook_Physics2D_OverlapBox
    {
        private static void Postfix(Vector2 __0, Vector2 __1, float __2, ContactFilter2D __3, Collider2D[] __4, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnPhysicsQuery(AreaShapeKind.Box, __0, 0f, __1, __2, CapsuleDirection2D.Vertical, __3, __4, null, ref __result, null); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(Physics2D), nameof(Physics2D.OverlapCapsule), new Type[] { typeof(Vector2), typeof(Vector2), typeof(CapsuleDirection2D), typeof(float), typeof(ContactFilter2D), typeof(Collider2D[]) })]
    internal static class AreaHook_Physics2D_OverlapCapsule
    {
        private static void Postfix(Vector2 __0, Vector2 __1, CapsuleDirection2D __2, float __3, ContactFilter2D __4, Collider2D[] __5, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnPhysicsQuery(AreaShapeKind.Capsule, __0, 0f, __1, __3, __2, __4, __5, null, ref __result, null); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(Collider2D), nameof(Collider2D.Overlap), new Type[] { typeof(ContactFilter2D), typeof(Collider2D[]) })]
    internal static class AreaHook_Collider2D_OverlapArray
    {
        private static void Postfix(Collider2D __instance, ContactFilter2D __0, Collider2D[] __1, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnPhysicsQuery(AreaShapeKind.Circle, Vector2.zero, 0f, Vector2.zero, 0f, CapsuleDirection2D.Vertical, __0, __1, null, ref __result, __instance); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(Collider2D), nameof(Collider2D.Overlap), new Type[] { typeof(ContactFilter2D), typeof(List<Collider2D>) })]
    internal static class AreaHook_Collider2D_OverlapList
    {
        private static void Postfix(Collider2D __instance, ContactFilter2D __0, List<Collider2D> __1, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnPhysicsQuery(AreaShapeKind.Circle, Vector2.zero, 0f, Vector2.zero, 0f, CapsuleDirection2D.Vertical, __0, null, __1, ref __result, __instance); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    // Single-hit raycasts. The game only ever calls the static Physics2D.Raycast(origin, direction,
    // distance, layerMask) wrapper (wall / tile checks everywhere, plus one boss laser that hits
    // Hitboxes), so that is the method hooked.
    //
    // DO NOT hook PhysicsScene2D.Raycast(...) (the struct instance method the wrapper forwards to)
    // or any other *struct* instance method that returns a struct: on this Mono runtime an instance
    // method of a value type receives its "this" pointer in the first argument register and the
    // hidden return-buffer pointer in the second, while the static replacement Harmony/MonoMod
    // detours it to expects them the other way round (MonoMod's ThiscallStructRetPtr glue only
    // covers class instance methods; for value types it stays at "Original"). The detoured raycast
    // then never writes its caller's result - every wall / tree / cliff collision test in
    // TopdownRigidbody silently missed and players walked through everything.
    [HarmonyPatch(typeof(Physics2D), nameof(Physics2D.Raycast), new Type[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) })]
    internal static class AreaHook_Physics2D_Raycast
    {
        private static void Postfix(Vector2 __0, Vector2 __1, float __2, int __3, RaycastHit2D __result)
        {
            if (!AreaRecorder.Armed) return;
            try
            {
                // what Unity's legacy layer-mask overload builds internally (ContactFilter2D.CreateLegacyFilter)
                ContactFilter2D f = default(ContactFilter2D);
                f.useTriggers = Physics2D.queriesHitTriggers;
                f.SetLayerMask(__3);
                f.SetDepth(float.NegativeInfinity, float.PositiveInfinity);
                AreaRecorder.OnRaycast(__0, __1, __2, f, __result);
            }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(TopdownSpatialHash), nameof(TopdownSpatialHash.CircleCastNonAlloc), new Type[] { typeof(Vector3), typeof(float), typeof(TopdownRigidbody[]) })]
    internal static class AreaHook_SpatialHash_CircleCast
    {
        private static void Postfix(Vector3 __0, float __1, TopdownRigidbody[] __2, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnGroundQuery(AreaShapeKind.Circle, __0, __1, Vector2.zero, 0f, __2, ref __result, null, AreaVictimTest.GroundPoint, true); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(TopdownSpatialHash), nameof(TopdownSpatialHash.BoxCastNonAlloc), new Type[] { typeof(Vector2), typeof(Vector2), typeof(TopdownRigidbody[]) })]
    internal static class AreaHook_SpatialHash_BoxCast
    {
        private static void Postfix(Vector2 __0, Vector2 __1, TopdownRigidbody[] __2, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnGroundQuery(AreaShapeKind.Box, __0, 0f, __1, 0f, __2, ref __result, null, AreaVictimTest.GroundPoint, false); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(TopdownSpatialHash), nameof(TopdownSpatialHash.BoxCastNonAlloc), new Type[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(TopdownRigidbody[]) })]
    internal static class AreaHook_SpatialHash_BoxCastAngle
    {
        private static void Postfix(Vector2 __0, Vector2 __1, float __2, TopdownRigidbody[] __3, ref int __result)
        {
            if (!AreaRecorder.Armed) return;
            try { AreaRecorder.OnGroundQuery(AreaShapeKind.Box, __0, 0f, __1, __2, __3, ref __result, null, AreaVictimTest.GroundPoint, false); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(TopdownSpatialHash), nameof(TopdownSpatialHash.Overlap), new Type[] { typeof(Collider2D), typeof(TopdownRigidbody[]) })]
    internal static class AreaHook_SpatialHash_Overlap
    {
        private static void Postfix(Collider2D __0, TopdownRigidbody[] __1, ref int __result)
        {
            if (!AreaRecorder.Armed || __0 == null) return;
            try { AreaRecorder.OnGroundQuery(AreaShapeKind.Circle, Vector3.zero, 0f, Vector2.zero, 0f, __1, ref __result, __0, AreaVictimTest.GroundCircle, false); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    [HarmonyPatch(typeof(TopdownSpatialHash), nameof(TopdownSpatialHash.ColliderCastNonAlloc), new Type[] { typeof(Collider2D), typeof(TopdownRigidbody[]) })]
    internal static class AreaHook_SpatialHash_ColliderCast
    {
        private static void Postfix(Collider2D __0, TopdownRigidbody[] __1, ref int __result)
        {
            if (!AreaRecorder.Armed || __0 == null) return;
            try { AreaRecorder.OnGroundQuery(AreaShapeKind.Circle, Vector3.zero, 0f, Vector2.zero, 0f, __1, ref __result, __0, AreaVictimTest.GroundPoint, false); }
            catch (Exception e) { AreaRecorder.LogHookError(e); }
        }
    }

    // =====================================================================================
    //  Client side
    // =====================================================================================

    public static class AreaVerifier
    {
        /// <summary>
        /// Decides, from the client's own view, whether the host's hit test would have found us.
        /// The shape is first moved to where we currently see the object it was attached to.
        /// Returns true (hit) whenever we cannot evaluate, so the host's verdict stands.
        /// </summary>
        /// <summary>
        /// If the client sees the anchor further than this from where the host had it, the object
        /// was teleported / re-used from a pool / resolved to the wrong child - re-anchoring would
        /// move the shape somewhere arbitrary, so the host's world position is used instead.
        /// </summary>
        private const float MaxAnchorDisplacement = 4f;

        public static bool Evaluate(AreaShape s, PlayerAvatar me, out string detail)
        {
            detail = "";
            if (s == null || me == null) return true;
            bool dbg = Plugin.DebugOn;
            string anchorInfo = "";
            if (s.hasAnchor && Plugin.ClientAreaHitAnchoring.Value)
            {
                Transform a = ResolveAnchor(s, me);
                if (a != null)
                {
                    Vector2 hostPos = s.anchorPos;
                    float moved = ((Vector2)a.position - hostPos).magnitude;
                    if (moved <= MaxAnchorDisplacement)
                    {
                        s.Rebase(a.position, a.eulerAngles.z, a.lossyScale.x < 0f);
                        if (dbg) anchorInfo = " anchor=" + a.name + " moved " + moved.ToString("0.00");
                    }
                    else if (dbg) anchorInfo = " anchor=" + a.name + " too far off (" + moved.ToString("0.0") + "), host position kept";
                }
                else if (dbg) anchorInfo = " anchor missing";
            }
            // Physics2D queries see collider poses as of the last physics step; make them current so
            // "where I am right now" is what gets tested.
            try { Physics2D.SyncTransforms(); } catch { }
            bool hit;
            switch (s.victim)
            {
                case AreaVictimTest.GroundPoint:
                {
                    TopdownRigidbody rb = me.TopdownRigidbody;
                    if (rb == null) { detail = "no rigidbody"; return true; }
                    hit = AreaGeom.ContainsPoint(s, rb.transform.position);
                    if (dbg) detail = s + anchorInfo + " me=" + (Vector2)rb.transform.position;
                    return hit;
                }
                case AreaVictimTest.GroundCircle:
                {
                    TopdownRigidbody rb = me.TopdownRigidbody;
                    if (rb == null) { detail = "no rigidbody"; return true; }
                    CircleCollider2D mc = rb.MovementCollider;
                    if (mc == null)
                    {
                        hit = AreaGeom.ContainsPoint(s, rb.transform.position);
                    }
                    else
                    {
                        Vector2 c; float r;
                        AreaGeom.MovementCircle(mc, out c, out r);
                        hit = AreaGeom.OverlapsCircle(s, c, r);
                    }
                    if (dbg) detail = s + anchorInfo + " me=" + (Vector2)rb.transform.position;
                    return hit;
                }
                default:
                {
                    Collider2D[] mine = AreaGeom.HitboxColliders(me);
                    if (mine.Length == 0) { detail = "no hitbox"; return true; }
                    hit = AreaGeom.HitboxOverlaps(s, mine);
                    if (dbg) detail = s + anchorInfo + " me=" + (Vector2)mine[0].bounds.center;
                    return hit;
                }
            }
        }

        private static Transform ResolveAnchor(AreaShape s, PlayerAvatar me)
        {
            NetworkIdentity ni;
            if (!NetworkClient.spawned.TryGetValue(s.anchorNetId, out ni) || ni == null) return null;
            if (me != null && ni == me.netIdentity) return null;   // never anchor to ourselves
            Transform t = ni.transform;
            for (int i = 0; i < s.anchorDepth; i++)
            {
                int idx = s.anchorPath[i];
                if (idx >= t.childCount) return null;
                t = t.GetChild(idx);
            }
            // host and client hierarchies under a networked object can differ (client-only FX children,
            // host-only helpers): a sibling path that lands on a differently named child is not the anchor
            if (t.name.GetStableHashCode() != s.anchorNameHash) return null;
            return t;
        }
    }
}
