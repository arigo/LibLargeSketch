using System;
using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// Displays a sketch consisting of triangles and stems.  Stems are edges, or line
/// segments, which may be drawn around the polygons or float on their own.  This
/// is optimized to handle very large sketches and supports relatively quick updates
/// (but not each-and-every-frame updates).
/// </summary>
/// <remark>
/// Despite what the API may suggest, this is optimized to be efficient for quite a
/// large amount of geometry.  This class is meant to be general and a bit low-level,
/// in that it receives triangles only; it does not handle the triangulation of more
/// general polygons.
///
/// Additions and removals of geometry might or might not be visible before Flush() is
/// called.  Disabling the whole GameObject during initial construction might be a way
/// to avoid partial results from showing up, if that is a problem.
///
/// All public methods can be called from a different thread than the main one, but
/// should not be called from multiple threads in parallel for the same LargeSketch
/// instance.  Use a lock if that could be the case.
///
/// If you need to minimize Z-fighting between different materials, one solution is to use
/// a material that comes with a Z offset and use a different offset for different
/// materials.  Also, the rendering of stems (edges) is sometimes hidden behind the
/// material of the triangles they are an edge of.  One solution to that is again Z offsets:
/// if the Z offset in the stem materials is always greater than the Z offset of the face
/// materials (e.g. because the latter is negative), the edges will be drawn on top.
///
/// Note that you can run into Unity limitations if the X, Y or Z values of 'positions'
/// are greater than a few thousand.  For example, in Unity 2017.3, if you use very large
/// values and then try to correct it by setting a very small scale on the GameObject,
/// it might render completely black.  More fundamentally, even if the scale is reasonable,
/// if all values are centered around a point that is many kilometers away from the origin,
/// then you'll run into precision issues in the 'float' type.  In theory, you should check
/// and fix both cases by storing internally values as a 3D 'double', and
/// scaling/offsetting them when converting to a Vector3.  (VR-Sketch-4 stores them as
/// 'double', but doesn't so far apply scaling/offsetting.)
/// </remark>

public class LargeSketch : MonoBehaviour
{
    public GameObject largeSketchMeshPrefab;
    public Material stemMaterial;

    /// <summary>
    /// Prepare new geometry with the given Material for the faces.
    /// 
    /// This method returns a GeometryBuilder on which you can add vertices and then create
    /// triangles or stems between them.  You could try to reuse previous vertices on the
    /// same GeometryBuilder, but it's not something VR-Sketch-4 does.
    /// 
    /// The returned GeometryBuilder structure also contains a unique id for this geometry,
    /// which can be saved and passed to ChangeMaterials() or RemoveGeometry().
    /// 
    /// Note that performance does NOT require you to draw many triangles inside a single
    /// PrepareGeometry() call.  Typically, every two-sided polygon is done with two calls to
    /// PrepareGeometry(), corresponding to the two sides.  The edges are added as stems
    /// during one of the two calls, not during extra calls, to reuse the vertices.  If the
    /// two sides have the same material then they can be combined into a single call to
    /// further reuse the vertices.  But apart from easy vertex reuse, there is no point in
    /// combining calls.
    /// </summary>
    public GeometryBuilder PrepareGeometry(Material face_mat)
    {
        MeshBuilder builder;
        if (!mesh_builders.TryGetValue(face_mat, out builder) || TryFinish(builder))
        {
            builder = new MeshBuilder(this, face_mat, 1);
            mesh_builders[face_mat] = builder;
        }
        return new GeometryBuilder
        {
            id = NextGeomId(builder),
            positions = builder.positions,
            normals = builder.normals,
            triangles = builder.triangles,
            stems = builder.stems,
        };
    }

    /// <summary>
    /// Same as PrepareGeometry(), but returns a StemsGeometryBuilder, which does not have
    /// the 'normals' or 'triangles' list.  For the free-floating stems in the model.
    ///
    /// Note that there is no PrepareGeometryXxx() method specifically for drawing
    /// triangles but not stems.  Just use 'PrepareGeometry(face_mat)' and never add
    /// any stem; this is basically just as efficient.
    /// </summary>
    public StemGeometryBuilder PrepareGeometryForStems()
    {
        MeshBuilder builder = mesh_builder_for_stems;
        if (builder == null || TryFinish(builder))
        {
            builder = new MeshBuilder(this, null, 0);
            mesh_builder_for_stems = builder;
        }
        return new StemGeometryBuilder
        {
            id = NextGeomId(builder),
            positions = builder.positions,
            stems = builder.stems,
        };
    }

    /// <summary>
    /// Same as PrepareGeometry(), but returns a TextureGeometryBuilder, which allows you
    /// (or more precisely, requires you) to also add UV coordinates for every vertex.
    /// </summary>
    public TextureGeometryBuilder PrepareGeometryWithTexture(Material face_mat)
    {
        MeshBuilder builder;
        if (!mesh_builders_with_texture.TryGetValue(face_mat, out builder) || TryFinish(builder))
        {
            builder = new MeshBuilder(this, face_mat, 2);
            mesh_builders_with_texture[face_mat] = builder;
        }
        return new TextureGeometryBuilder
        {
            id = NextGeomId(builder),
            positions = builder.positions,
            normals = builder.normals,
            uvs = builder.uvs,
            triangles = builder.triangles,
            stems = builder.stems,
        };
    }


    public struct GeometryBuilder
    {
        /// <summary> Unique id. </summary>
        public int id;

        /// <summary>
        /// List of vertices.  After PrepareGeometry(), you add your vertex positions here.
        /// </summary>
        public List<Vector3> positions;

        /// <summary>
        /// List of normals.  After PrepareGeometry(), you add your vertex normals here.
        /// This list must always have the same length as 'positions'.
        /// </summary>
        public List<Vector3> normals;

        /// <summary>
        /// Integer list of triangles.  Put three numbers for each triangle, which are
        /// indexes in the 'positions' and 'normals' lists.
        /// </summary>
        public List<int> triangles;

        /// <summary>
        /// Integer list of stems.  Put two numbers for each stem, which are indexes
        /// in the 'positions' list.
        /// </summary>
        public List<int> stems;
    }


    public struct StemGeometryBuilder
    {
        /// <summary> Unique id. </summary>
        public int id;
        /// <summary> See GeometryBuilder.positions </summary>
        public List<Vector3> positions;
        /// <summary> See GeometryBuilder.stems </summary>
        public List<int> stems;
    }


    public struct TextureGeometryBuilder
    {
        /// <summary> Unique id. </summary>
        public int id;
        /// <summary> See GeometryBuilder.positions </summary>
        public List<Vector3> positions;
        /// <summary> See GeometryBuilder.normals </summary>
        public List<Vector3> normals;
        /// <summary> See GeometryBuilder.triangles </summary>
        public List<int> triangles;
        /// <summary> See GeometryBuilder.stems </summary>
        public List<int> stems;

        /// <summary>
        /// List of UV coordinates.  This list must always have the same length as
        /// 'positions' and 'normals'.
        /// </summary>
        public List<Vector2> uvs;
    }


    /// <summary>
    /// Removes the geometry added after the corresponding PrepareGeometry() call.
    /// 
    /// After calling RemoveGeometry() the id is invalid and should not be used any more.
    /// It could be reused by future PrepareGeometry() calls (even before the next
    /// Flush(), in multithreaded situations).
    /// </summary>
    public void RemoveGeometry(int id)
    {
        update_renderer_gindex.Add(geometries[id].gindex);
        geometries[id].gindex = GINDEX_DESTROYED;
    }

    /// <summary>
    /// Temporarily hide the geometry added after the corresponding PrepareGeometry() call.
    /// 
    /// This is a no-op if it is already hidden.
    /// </summary>
    public void HideGeometry(int id)
    {
        int gindex = geometries[id].gindex;
        update_renderer_gindex.Add(gindex);
        gindex |= GINDEX_HIDDEN;
        geometries[id].gindex = gindex;
    }

    /// <summary>
    /// Cancel the effect of HideGeometry().
    /// 
    /// This is a no-op if it is already visible.  This is the default state.
    /// </summary>
    public void ShowGeometry(int id)
    {
        int gindex = geometries[id].gindex;
        update_renderer_gindex.Add(gindex);
        gindex &= ~GINDEX_HIDDEN;
        geometries[id].gindex = gindex;
    }


    public delegate void OnReady();

    /// <summary>
    /// Schedule flushing of the pending additions, removals, and shows/hides.
    /// The actual construction or updating of meshes will occur during the next
    /// LateUpdate().
    /// 
    /// If 'onReady' is given, then this will be called in the main thread after
    /// the construction is complete.
    /// </summary>
    public void Flush(OnReady onReady = null)
    {
        foreach (var builder in mesh_builders.Values)
            Finish(builder);
        mesh_builders.Clear();

        foreach (var builder in mesh_builders_with_texture.Values)
            Finish(builder);
        mesh_builders_with_texture.Clear();

        if (mesh_builder_for_stems != null)
        {
            Finish(mesh_builder_for_stems);
            mesh_builder_for_stems = null;
        }

        if (update_renderer_gindex.Count > 0)
        {
            foreach (var gindex in new List<int>(update_renderer_gindex))
            {
                Debug.Assert((gindex & GINDEX_FREE) == 0);
                Debug.Assert(gindex != GINDEX_DESTROYED);
                Debug.Assert(gindex != (GINDEX_DESTROYED ^ GINDEX_HIDDEN));

                if ((gindex & GINDEX_HIDDEN) == GINDEX_HIDDEN)
                {
                    update_renderer_gindex.Remove(gindex);
                    update_renderer_gindex.Add(gindex & ~GINDEX_HIDDEN);
                }
            }
            var lst = new List<int>(update_renderer_gindex);
            EnqueueUpdater(new RendererUpdater(lst));
            update_renderer_gindex.Clear();
        }

        if (onReady != null)
            EnqueueUpdater(new ReadyUpdater(onReady));
    }


    /***************************** IMPLEMENTATION *****************************/


    interface IMeshUpdater
    {
        void MainThreadUpdate(LargeSketch sketch);
    }

    class MeshBuilder : IMeshUpdater
    {
        internal Material face_mat;
        internal List<Vector3> positions;
        internal List<Vector3> normals;
        internal List<Vector2> uvs;
        internal List<int> triangles;
        internal List<int> stems;

        internal List<int> geom_ids;
        internal int current_geom_id = -1;
        internal int renderer_index;
        internal int[] triangles_final, stems_final;

        internal MeshBuilder(LargeSketch sketch, Material face_mat, int level)
        {
            this.face_mat = face_mat;

            positions = new List<Vector3>();
            if (level >= 1) normals = new List<Vector3>();
            if (level >= 2) uvs = new List<Vector2>();
            triangles = new List<int>();    // if level == 0, should remain empty
            stems = new List<int>();

            geom_ids = new List<int>();
            renderer_index = sketch.NewRendererIndex();
        }

        public void MainThreadUpdate(LargeSketch sketch)
        {
            var mgos = sketch.mgos;
            int index = renderer_index;
            while (!(index < mgos.Count))
                mgos.Add(null);
            Debug.Assert(mgos[index] == null);
            mgos[index] = new MeshGameObject(sketch, this);
        }
    }

    class RendererUpdater : IMeshUpdater
    {
        List<int> renderers;

        internal RendererUpdater(List<int> renderers)
        {
            this.renderers = renderers;
        }

        public void MainThreadUpdate(LargeSketch sketch)
        {
            foreach (var renderer_index in renderers)
            {
                var mgo = sketch.mgos[renderer_index];
                mgo.UpdateRenderer();

                if (mgo.IsDefinitelyEmpty())
                {
                    sketch.mgos[renderer_index] = null;
                    sketch.AddFreeRendererIndex(renderer_index);
                }
            }
        }
    }

    class ReadyUpdater : IMeshUpdater
    {
        OnReady onReady;

        internal ReadyUpdater(OnReady onReady)
        {
            this.onReady = onReady;
        }

        public void MainThreadUpdate(LargeSketch sketch)
        {
            onReady();
        }
    }


    const int GINDEX_FREE = 0x01;
    const int GINDEX_HIDDEN = 0x02;
    const int GINDEX_SHIFT = 2;
    const int GINDEX_FREE_SHIFT = 1;

    const int GINDEX_DESTROYED = ((-1) << GINDEX_SHIFT) | GINDEX_HIDDEN;

    struct Geom
    {
        internal int gindex;
        internal ushort triangle_start, triangle_stop;
        internal ushort stem_start, stem_stop;

        /* Each of the ranges stores information in two 16-bit values: the 'start' 16 bits are
         * the index of the starting point (counted as whole triangles/stems, not as indices
         * inside the 'triangles' or 'stems' list); and the 'stop' 16 bits are the first value
         * after the range.  If stops are 0xFFFF, then it means actually that the range goes up
         * to the end, which may be below or above 65535.  The start is always below 65535.
         */
    }

    Dictionary<Material, MeshBuilder> mesh_builders = new Dictionary<Material, MeshBuilder>();
    Dictionary<Material, MeshBuilder> mesh_builders_with_texture = new Dictionary<Material, MeshBuilder>();
    MeshBuilder mesh_builder_for_stems = null;
    HashSet<int> update_renderer_gindex = new HashSet<int>();
    Queue<IMeshUpdater> mesh_builders_ready = new Queue<IMeshUpdater>();
    volatile bool mesh_builders_ready_flag;

    Geom[] geometries = new Geom[0];
    int geom_free_head = -1;
    int geom_free_head_mainthread = -1;
    object geometries_lock = new object();  // protects the 'geometries' and 'geom_free_head_mainthread' fields


    int NextGeomId(MeshBuilder builder)
    {
        int result = geom_free_head;
        if (result < 0)
            result = FillMoreGeoms();
        geom_free_head = geometries[result].gindex >> GINDEX_FREE_SHIFT;

        geometries[result].gindex = builder.renderer_index << GINDEX_SHIFT;
        geometries[result].triangle_start = (ushort)((uint)builder.triangles.Count / 3);
        geometries[result].stem_start = (ushort)((uint)builder.stems.Count / 2);
        builder.geom_ids.Add(result);
        builder.current_geom_id = result;
        return result;
    }

    int FillMoreGeoms()
    {
        lock (geometries_lock)
        {
            if (geom_free_head_mainthread >= 0)
            {
                geom_free_head = geom_free_head_mainthread;
                geom_free_head_mainthread = -1;
            }
            else
            {
                int cnt = geometries.Length;
                Array.Resize(ref geometries, (cnt + 22) * 3 / 2);
                for (int i = geometries.Length - 1; i >= cnt; i--)
                {
                    geometries[i].gindex = (geom_free_head << GINDEX_FREE_SHIFT) | GINDEX_FREE;
                    geom_free_head = i;
                }
            }
        }
        return geom_free_head;
    }

    bool TryFinish(MeshBuilder builder)
    {
        uint n1 = (uint)builder.positions.Count;
        uint n2 = (uint)builder.triangles.Count / 3;
        uint n3 = (uint)builder.stems.Count / 2;
        if ((n1 | n2 | n3) > 0x7FFF)
        {
            Finish(builder);
            return true;
        }
        else
        {
            int id = builder.current_geom_id;
            builder.current_geom_id = -1;
            geometries[id].triangle_stop = (ushort)n2;
            geometries[id].stem_stop = (ushort)n3;
            return false;
        }
    }

    void Finish(MeshBuilder builder)
    {
        int id = builder.current_geom_id;
        builder.current_geom_id = -1;
        geometries[id].triangle_stop = 0xFFFF;
        geometries[id].stem_stop = 0xFFFF;

        Debug.Assert(builder.normals == null || builder.positions.Count == builder.normals.Count);
        Debug.Assert(builder.uvs == null || builder.positions.Count == builder.uvs.Count);
        Debug.Assert((uint)builder.triangles.Count % 3 == 0);
        Debug.Assert((uint)builder.stems.Count % 2 == 0);

        builder.triangles_final = builder.triangles.ToArray(); builder.triangles = null;
        builder.stems_final = builder.stems.ToArray(); builder.stems = null;

        EnqueueUpdater(builder);
    }

    void EnqueueUpdater(IMeshUpdater updater)
    {
        lock (mesh_builders_ready)
            mesh_builders_ready.Enqueue(updater);
        mesh_builders_ready_flag = true;
    }


    List<int> mgos_free_list = new List<int>();
    int mgos_allocated_count = 0;

    int NewRendererIndex()
    {
        int result;
        lock (mgos_free_list)
        {
            if (mgos_free_list.Count > 0)
            {
                result = mgos_free_list[mgos_free_list.Count - 1];
                mgos_free_list.RemoveAt(mgos_free_list.Count - 1);
            }
            else
            {
                result = mgos_allocated_count++;
            }
        }
        return result;
    }

    void AddFreeRendererIndex(int renderer_index)
    {
        lock (mgos_free_list)
            mgos_free_list.Add(renderer_index);
    }


    /***************************** Main thread gameobject parts *****************************/
    /***             Everything below is accessed only by the main thread.                ***/


    List<MeshGameObject> mgos = new List<MeshGameObject>();


    class MeshGameObject
    {
        LargeSketch sketch;
        Mesh mesh;
        MeshRenderer renderer;
        List<int> geom_ids;
        Material face_mat;
        int[] all_triangles, all_stems;


        internal MeshGameObject(LargeSketch sketch, MeshBuilder builder)
        {
            this.sketch = sketch;
            geom_ids = builder.geom_ids;
            face_mat = builder.face_mat;
            all_triangles = builder.triangles_final;
            all_stems = builder.stems_final;

            mesh = new Mesh();

            if (builder.positions.Count >= 0xFFE0)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(builder.positions);

            if (builder.normals != null)
                mesh.SetNormals(builder.normals);

            if (builder.uvs != null)
                mesh.SetUVs(0, builder.uvs);

            UpdateRenderer();
        }

        internal void UpdateRenderer()
        {
            List<int> triangles = new List<int>(capacity: all_triangles.Length);
            List<int> stems = new List<int>(capacity: all_stems.Length);

            lock (sketch.geometries_lock)
            {
                var geometries = sketch.geometries;

                for (int i = geom_ids.Count - 1; i >= 0; --i)
                {
                    int geom_id = geom_ids[i];
                    Geom geom = geometries[geom_id];
                    /* Concurrent writes to 'geom' could occur, but only the 'gindex' field, and
                     * only to add/remove the GINDEX_HIDDEN flag or to change it to GINDEX_DESTROYED.
                     * If that occurs, this MeshGameObject will be scheduled for another update
                     * at the next Flush() anyway.
                     */
                    int gindex = geom.gindex;

                    if ((gindex & GINDEX_HIDDEN) == 0)
                    {
                        /* Geometry is visible */
                        int start, stop;
                        start = geom.triangle_start * 3;
                        stop = geom.triangle_stop * 3;
                        if (stop == 0xFFFF * 3)
                            stop = all_triangles.Length;
                        for (int j = start; j < stop; j++)
                            triangles.Add(all_triangles[j]);

                        start = geom.stem_start * 2;
                        stop = geom.stem_stop * 2;
                        if (stop == 0xFFFF * 2)
                            stop = all_stems.Length;
                        for (int j = start; j < stop; j++)
                            stems.Add(all_stems[j]);
                    }
                    else if (gindex == GINDEX_DESTROYED)
                    {
                        /* Geom was destroyed */
                        int last = geom_ids.Count - 1;
                        geom_ids[i] = geom_ids[last];
                        geom_ids.Remove(last);

                        geometries[geom_id].gindex = 
                            (sketch.geom_free_head_mainthread << GINDEX_FREE_SHIFT) | GINDEX_FREE;
                        sketch.geom_free_head_mainthread = geom_id;
                    }
                }
            }
            geom_ids.TrimExcess();

            bool any_triangle = triangles.Count > 0;
            bool any_stem = stems.Count > 0;

            if (any_triangle || any_stem)
            {
                mesh.subMeshCount = (any_triangle ? 1 : 0) + (any_stem ? 1 : 0);
                Material[] mats = new Material[mesh.subMeshCount];
                int submesh = 0;

                if (any_triangle)
                {
                    mesh.SetTriangles(triangles, submesh, calculateBounds: false);
                    mats[submesh] = face_mat;
                    submesh++;
                }
                if (any_stem)
                {
                    mesh.SetIndices(stems.ToArray(), MeshTopology.Lines, submesh, calculateBounds: false);
                    mats[submesh] = sketch.stemMaterial;
                    submesh++;
                }

                mesh.RecalculateBounds();

                if (renderer == null)
                {
                    var go = Instantiate(sketch.largeSketchMeshPrefab, sketch.transform);
                    go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    renderer = go.GetComponent<MeshRenderer>();
                }
                renderer.sharedMaterials = mats;
            }
            else
            {
                if (renderer != null)
                {
                    Destroy(renderer.gameObject);
                    renderer = null;
                }
            }
        }

        internal bool IsDefinitelyEmpty()
        {
            return geom_ids.Count == 0;
        }
    }

    void CallRegularUpdate()
    {
        if (!mesh_builders_ready_flag)
            return;

        List<IMeshUpdater> updaters;

        lock (mesh_builders_ready)
        {
            updaters = new List<IMeshUpdater>(mesh_builders_ready);
            mesh_builders_ready.Clear();
            mesh_builders_ready_flag = false;
        }

        foreach (var updater in updaters)
            updater.MainThreadUpdate(this);
    }


    /***************************** LargeSketchUpdater object *****************************/
    /***  A separate GameObject just to ensure LateUpdate() runs even if the original  ***/
    /***  LargeSketch is disabled                                                      ***/


    class LargeSketchUpdater : MonoBehaviour
    {
        internal LargeSketch sketch;

        private void LateUpdate()
        {
            sketch.CallRegularUpdate();
        }
    }
    LargeSketchUpdater updater;

    private void Awake()
    {
        var go = new GameObject(gameObject.name + " (updater)");
        updater = go.AddComponent<LargeSketchUpdater>();
        updater.sketch = this;
    }

    private void OnDestroy()
    {
        if (updater != null)
        {
            Destroy(updater.gameObject);
            updater = null;
        }
    }
}
