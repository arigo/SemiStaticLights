using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


[ExecuteInEditMode]
public class SemiStaticLights : MonoBehaviour
{
    public Light directionalLight;
    public LayerMask cullingMask = -1;
    public int gridResolution = 32;
    public float gridPixelSize = 1/16f;
    public int numCascades = 5;

    public Shader gvShader;
    public ComputeShader gvCompute;

    public bool drawGizmosGV;
    public bool drawGizmosLT;
    public int drawCascadeLevel = 0;
    public int drawLightingViewRay = 0;


    class ViewRay
    {
        internal Vector3 world_forward;
        internal Matrix4x4 world_to_light_local_matrix;
        internal RenderTexture lighting_tower;    /* concatenation of 'numCascades' cubes along the Z axis */
    }

    Vector3 _light_center;
    Camera _shadowCam;
    Matrix4x4 _world_to_light_local_matrix;
    Matrix4x4[] _scale_for_cascade_matrix;
    Matrix4x4 _light_local_to_world_matrix;
    Vector3 _light_forward;
    RenderTexture[] _tex3d_gvs = Array.Empty<RenderTexture>();
    ViewRay[] _view_rays = _InitViewRays();

    static ViewRay[] _InitViewRays()
    {
        var result = new ViewRay[18];
        for (int i = 0; i < result.Length; i++)
            result[i] = new ViewRay();
        return result;
    }


    void DestroyTarget(ref RenderTexture tex)
    {
        if (tex)
            DestroyImmediate(tex);
        tex = null;
    }

    void DestroyTargets()
    {
        for (int i = 0; i < _tex3d_gvs.Length; i++)
            DestroyTarget(ref _tex3d_gvs[i]);
        foreach (var view_ray in _view_rays)
            DestroyTarget(ref view_ray.lighting_tower);
    }

    private void OnDestroy()
    {
        DestroyTargets();
    }


#if UNITY_EDITOR
    private void Update()
    {
        if (!Application.isPlaying)
            ComputeLightBounces();
    }
#endif

    public void ComputeLightBounces(Vector3? center = null)
    {
        _light_center = center ?? transform.position;
        if (directionalLight == null || gridResolution <= 0 || gridPixelSize <= 0f || numCascades <= 0)
            return;

        if (_tex3d_gvs.Length != numCascades || _tex3d_gvs[0].width != gridResolution ||
            _view_rays[0].lighting_tower == null || !_view_rays[0].lighting_tower.IsCreated())
        {
            DestroyTargets();
            _tex3d_gvs = new RenderTexture[numCascades];
            for (int i = 0; i < numCascades; i++)
                _tex3d_gvs[i] = CreateTex3dGV();
            foreach (var view_ray in _view_rays)
                view_ray.lighting_tower = CreateLightingTower();
        }

        /* Render the geometry voxels, the whole cascade.  This builds a cascade of 3D textures
         * with each voxel being just an 'R8'.  The value is between 0.0 (opaque voxel) and 1.0
         * (transparent voxel). */
        RenderGV();

        for (int orientation = 0; orientation < 3; orientation++)
        {
            /* Directionally copy the vertices to larger cascade levels.  This overwrites the
             * 1/8th central part of each 3D texture with voxels computed from the next smaller
             * level.  The computation is done directionally, according to the orientation.
             * It starts with the 8 smaller voxels that cover the single bigger voxel.  It
             * computes 4 times the product of two voxels, to get the opacity along all 4 pairs of
             * voxels in the fixed direction.  The final big voxel value is the mean of these
             * 4 intermediate values.
             */
            DirectionalCopyGV(orientation);

            PropagateLight(2 * orientation);
        }

        /*ShowCascade(numCascades - 1, ray_index: 0);*/
    }

    /*void ShowCascade(int cascade, int ray_index)
    {
        Vector4 show_cascade;
        show_cascade.x = Mathf.Pow(0.5f, cascade);
        show_cascade.y = Mathf.Pow(0.5f, cascade);
        show_cascade.z = Mathf.Pow(0.5f, cascade) / numCascades;
        show_cascade.w = cascade / (float)numCascades;
        Shader.SetGlobalVector("_LPV_ShowCascade", show_cascade);

        var mat = _world_to_light_local_matrix[0];
        Shader.SetGlobalMatrix("_LPV_WorldToLightLocalMatrix", mat);

        Shader.SetGlobalTexture("_LPV_LightingTower", _view_rays[ray_index].lighting_tower);
    }*/

    RenderTexture CreateTex3dGV()
    {
        var desc = new RenderTextureDescriptor(gridResolution, gridResolution, RenderTextureFormat.R8);
        desc.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        desc.volumeDepth = gridResolution;
        desc.enableRandomWrite = true;
        desc.useMipMap = false;
        desc.autoGenerateMips = false;

        RenderTexture tg = new RenderTexture(desc);
        tg.wrapMode = TextureWrapMode.Clamp;
        tg.filterMode = FilterMode.Point;
        return tg;
    }

    RenderTexture CreateLightingTower()
    {
        var desc = new RenderTextureDescriptor(gridResolution, gridResolution, RenderTextureFormat.ARGB32);
        desc.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        desc.volumeDepth = gridResolution * numCascades;
        desc.enableRandomWrite = true;
        desc.useMipMap = false;
        desc.autoGenerateMips = false;

        RenderTexture tg = new RenderTexture(desc);
        tg.wrapMode = TextureWrapMode.Clamp;
        tg.filterMode = FilterMode.Bilinear;
        return tg;
    }

    void RenderGV()
    {
        var cam = FetchShadowCamera();

        _world_to_light_local_matrix = cam.transform.worldToLocalMatrix;
        _scale_for_cascade_matrix = new Matrix4x4[numCascades];

        for (int i = 0; i < numCascades; i++)
        {
            float pixel_size = gridPixelSize * Mathf.Pow(2f, i);
            float half_size = 0.5f * gridResolution * pixel_size;

            var mat = Matrix4x4.Scale(Vector3.one / (2f * half_size)) *
                      Matrix4x4.Translate(Vector3.one * half_size);
            _scale_for_cascade_matrix[i] = mat;
            mat *= _world_to_light_local_matrix;

            /* Render into the GV (geometry volume).  Here, there is no depth map
             * and the fragment shader writes into the ComputeBuffer cb_gv.  At the end we
             * copy the information into the RenderTexture tex3d_gv.
             */
            var cb_gv = new ComputeBuffer(gridResolution * gridResolution * gridResolution, 4);
            int clear_kernel = gvCompute.FindKernel("SetToOnes");
            gvCompute.SetInt("GridResolution", gridResolution);
            gvCompute.SetBuffer(clear_kernel, "RSM_gv", cb_gv);
            int thread_groups = (gridResolution * gridResolution * gridResolution + 63) / 64;
            gvCompute.Dispatch(clear_kernel, thread_groups, 1, 1);

            Shader.SetGlobalInt("_LPV_GridResolution", gridResolution);
            Shader.SetGlobalMatrix("_LPV_WorldToLightLocalMatrix", mat);

            RenderTexture rt_temp = RenderTexture.GetTemporary(gridResolution, gridResolution, 0,
                                                               RenderTextureFormat.R8);

            cam.orthographicSize = half_size;
            cam.nearClipPlane = -half_size;
            cam.farClipPlane = half_size;
            cam.targetTexture = rt_temp;
            cam.clearFlags = CameraClearFlags.Nothing;
            Graphics.SetRandomWriteTarget(1, cb_gv);

            var orig_position = cam.transform.position;
            var orig_rotation = cam.transform.rotation;
            var axis_x = cam.transform.right;
            var axis_y = cam.transform.up;
            var axis_z = cam.transform.forward;
            //cam.transform.position -= 0.5f * pixel_size * (axis_x + axis_y + axis_z);
            cam.RenderWithShader(gvShader, "RenderType");
            cam.transform.SetPositionAndRotation(orig_position, orig_rotation);

            cam.transform.Rotate(axis_y, -90, Space.World);
            cam.transform.Rotate(axis_z, -90, Space.World);
            //cam.transform.position -= 0.5f * pixel_size * (axis_z + axis_x + axis_y);
            Shader.EnableKeyword("ORIENTATION_2");
            cam.RenderWithShader(gvShader, "RenderType");
            Shader.DisableKeyword("ORIENTATION_2");
            cam.transform.SetPositionAndRotation(orig_position, orig_rotation);

            cam.transform.Rotate(axis_z, 90, Space.World);
            cam.transform.Rotate(axis_y, 90, Space.World);
            //cam.transform.position -= 0.5f * pixel_size * (axis_y + axis_z + axis_x);
            Shader.EnableKeyword("ORIENTATION_3");
            cam.RenderWithShader(gvShader, "RenderType");
            Shader.DisableKeyword("ORIENTATION_3");
            cam.transform.SetPositionAndRotation(orig_position, orig_rotation);

            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt_temp);

            int pack_kernel = gvCompute.FindKernel("PackToTexture");
            gvCompute.SetBuffer(pack_kernel, "RSM_gv", cb_gv);
            _tex3d_gvs[i].Create();
            gvCompute.SetTexture(pack_kernel, "LPV_gv", _tex3d_gvs[i]);
            thread_groups = (gridResolution + 3) / 4;
            gvCompute.Dispatch(pack_kernel, thread_groups, thread_groups, thread_groups);
            cb_gv.Release();
        }
        _light_local_to_world_matrix = Matrix4x4.Inverse(_world_to_light_local_matrix);
    }

    void DirectionalCopyGV(int orientation)
    {
        /* orientation is 0 for X (right), 1 for Y (up), 2 for Z (forward) of the shadow camera
         */
        Vector3 DX = Vector3.zero;
        Vector3 DY = Vector3.zero;
        Vector3 DZ = Vector3.zero;
        DZ[orientation] = 1f;
        DX[(orientation + 1) % 3] = 1f;
        DY[(orientation + 2) % 3] = 1f;

        _light_forward = (_light_local_to_world_matrix * DZ).normalized;

        int directional_copy_kernel = gvCompute.FindKernel("DirectionalCopy");
        gvCompute.SetInts("DX", (int)DX.x, (int)DX.y, (int)DX.z);
        gvCompute.SetInts("DY", (int)DY.x, (int)DY.y, (int)DY.z);
        gvCompute.SetInts("DZ", (int)DZ.x, (int)DZ.y, (int)DZ.z);

        for (int i = 1; i < numCascades; i++)
        {
            gvCompute.SetTexture(directional_copy_kernel, "Input_gv", _tex3d_gvs[i - 1]);
            gvCompute.SetTexture(directional_copy_kernel, "LPV_gv", _tex3d_gvs[i]);
            int thread_groups = (gridResolution + 7) / 8;
            gvCompute.Dispatch(directional_copy_kernel, thread_groups, thread_groups, thread_groups);
        }
    }

    void SetAmbientProbe()
    {
        Color[] results = new Color[2];
        RenderSettings.ambientProbe.Evaluate(new Vector3[] { -_light_forward, _light_forward }, results);
        gvCompute.SetVector("AmbientForward", results[0]);
        gvCompute.SetVector("AmbientBackward", results[1]);
    }

    void PropagateLight(int ray_index)
    {
        /* 'ray_index' is an index into the _view_rays array.
         * This computes and fills both _view_rays[ray_index] and _view_rays[ray_index + 1]
         * along the "_light_forward" and "-_light_forward" directions.
         */
        Debug.Assert((ray_index & 1) == 0);
        _view_rays[ray_index].world_forward = _light_forward;
        _view_rays[ray_index].world_to_light_local_matrix = _world_to_light_local_matrix;
        _view_rays[ray_index].lighting_tower.Create();
        _view_rays[ray_index + 1].world_forward = -_light_forward;
        _view_rays[ray_index + 1].world_to_light_local_matrix = _world_to_light_local_matrix;
        _view_rays[ray_index + 1].lighting_tower.Create();

        SetAmbientProbe();

        int thread_groups = (gridResolution + 3) / 4;
        int propagate_kernel = gvCompute.FindKernel("PropagateFromAmbient");
        gvCompute.SetTexture(propagate_kernel, "Input_gv", _tex3d_gvs[numCascades - 1]);
        gvCompute.SetTexture(propagate_kernel, "LightingTowerForward", _view_rays[ray_index].lighting_tower);
        gvCompute.SetTexture(propagate_kernel, "LightingTowerBackward", _view_rays[ray_index + 1].lighting_tower);
        gvCompute.SetInts("CascadeZIndex", -1, -1, -1, (numCascades - 1) * gridResolution);
        gvCompute.Dispatch(propagate_kernel, thread_groups, thread_groups, thread_groups);

        propagate_kernel = gvCompute.FindKernel("PropagateFromUpperLevel");
        gvCompute.SetTexture(propagate_kernel, "LightingTowerForward", _view_rays[ray_index].lighting_tower);
        gvCompute.SetTexture(propagate_kernel, "LightingTowerBackward", _view_rays[ray_index + 1].lighting_tower);

        for (int i = numCascades - 2; i >= 0; --i)
        {
            gvCompute.SetTexture(propagate_kernel, "Input_gv", _tex3d_gvs[i]);
            gvCompute.SetTexture(propagate_kernel, "UpperLevelInput_gv", _tex3d_gvs[i + 1]);
            gvCompute.SetInts("CascadeZIndex",
                (gridResolution >> 2),
                (gridResolution >> 2),
                (gridResolution >> 2) + (i + 1) * gridResolution,
                i * gridResolution);
            gvCompute.Dispatch(propagate_kernel, thread_groups, thread_groups, thread_groups);
        }
    }

    Camera FetchShadowCamera()
    {
        if (_shadowCam == null)
        {
            // Create the shadow rendering camera
            GameObject go = new GameObject("RSM shadow cam (not saved)");
            //go.hideFlags = HideFlags.HideAndDontSave;
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform);

            _shadowCam = go.AddComponent<Camera>();
            _shadowCam.orthographic = true;
            _shadowCam.enabled = false;
            /* the shadow camera renders to four components:
             *    r, g, b: surface normal vector
             *    a: depth, in [-0.5, 0.5] with 0.0 being at the camera position
             *              and larger values being farther from the light source
             */
            _shadowCam.backgroundColor = new Color(0, 0, 0, 1);
            _shadowCam.aspect = 1;
            /* Obscure: if the main camera is stereo, then this one will be confused in
             * the SetTargetBuffers() mode unless we force it to not be stereo */
            _shadowCam.stereoTargetEye = StereoTargetEyeMask.None;
        }
        _shadowCam.cullingMask = cullingMask;
        _shadowCam.transform.SetPositionAndRotation(_light_center, directionalLight.transform.rotation);
        _shadowCam.transform.localScale = Vector3.one;
        return _shadowCam;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (drawGizmosGV)
        {
            if (_tex3d_gvs != null && drawCascadeLevel >= 0 && drawCascadeLevel < _tex3d_gvs.Length &&
                _scale_for_cascade_matrix != null && drawCascadeLevel < _scale_for_cascade_matrix.Length)
                DrawGizmosGV(_tex3d_gvs[drawCascadeLevel], _scale_for_cascade_matrix[drawCascadeLevel] * _world_to_light_local_matrix);
        }
        if (drawGizmosLT)
        {
            if (drawLightingViewRay >= 0 && drawLightingViewRay < _view_rays.Length &&
                _scale_for_cascade_matrix != null && drawCascadeLevel < _scale_for_cascade_matrix.Length)
                DrawGizmosLT(_view_rays[drawLightingViewRay], drawCascadeLevel);
        }
    }

    void DrawGizmosExtract(out float[] array, RenderTexture rt)
    {
        var buffer = new ComputeBuffer(gridResolution * gridResolution * gridResolution, 4, ComputeBufferType.Default);
        int unpack_kernel = gvCompute.FindKernel("UnpackFromTexture");
        gvCompute.SetBuffer(unpack_kernel, "RSM_gv", buffer);
        gvCompute.SetTexture(unpack_kernel, "LPV_gv", rt);
        gvCompute.SetInt("GridResolution", gridResolution);

        int thread_groups = (gridResolution + 3) / 4;
        gvCompute.Dispatch(unpack_kernel, thread_groups, thread_groups, thread_groups);

        array = new float[gridResolution * gridResolution * gridResolution];
        buffer.GetData(array);
        buffer.Release();
    }

    void DrawGizmosGV(RenderTexture rt, Matrix4x4 world2lightlocal)
    {
        if (rt == null || !rt.IsCreated())
            return;
        DrawGizmosExtract(out var array, rt);

        Gizmos.matrix = Matrix4x4.Inverse(world2lightlocal) * Matrix4x4.Scale(Vector3.one / gridResolution);

        var gizmos = new List<Tuple<Vector3, float>>();

        const float dd = 0.5f;
        int index = 0;
        for (int z = 0; z < gridResolution; z++)
            for (int y = 0; y < gridResolution; y++)
                for (int x = 0; x < gridResolution; x++)
                {
                    float entry = array[index++];
                    if (entry != 1f)
                    {
                        gizmos.Add(System.Tuple.Create(
                            new Vector3(x + dd, y + dd, z + dd),
                            entry));
                    }
                }

        var camera_fwd = UnityEditor.SceneView.lastActiveSceneView.camera.transform.forward;
        camera_fwd = Matrix4x4.Inverse(Gizmos.matrix).MultiplyVector(camera_fwd);
        gizmos.Sort((g1, g2) =>
        {
            var d1 = Vector3.Dot(camera_fwd, g1.Item1);
            var d2 = Vector3.Dot(camera_fwd, g2.Item1);
            return d2.CompareTo(d1);
        });
        Gizmos.color = new Color(0.35f, 0.35f, 0.35f);
        foreach (var g in gizmos)
        {
            float size = (1f - g.Item2) * 0.5f;
            Gizmos.DrawCube(g.Item1, Vector3.one * size);
        }
    }

    void DrawGizmosExtractLT(out float[] array, RenderTexture rt, int zoffset)
    {
        var buffer = new ComputeBuffer(gridResolution * gridResolution * gridResolution * 3, 4, ComputeBufferType.Default);
        int unpack_kernel = gvCompute.FindKernel("UnpackFromLightingTower");
        gvCompute.SetBuffer(unpack_kernel, "RSM_gv", buffer);
        gvCompute.SetTexture(unpack_kernel, "LightingTowerForward", rt);
        gvCompute.SetInt("GridResolution", gridResolution);
        gvCompute.SetInts("CascadeZIndex", 0, 0, zoffset, 0);

        int thread_groups = (gridResolution + 3) / 4;
        gvCompute.Dispatch(unpack_kernel, thread_groups, thread_groups, thread_groups);

        array = new float[gridResolution * gridResolution * gridResolution * 3];
        buffer.GetData(array);
        buffer.Release();
    }

    void DrawGizmosLT(ViewRay view_ray, int cascade)
    {
        var rt = view_ray.lighting_tower;
        if (rt == null || !rt.IsCreated())
            return;
        DrawGizmosExtractLT(out var array, rt, cascade * gridResolution);

        Gizmos.matrix =
            Matrix4x4.Inverse(_scale_for_cascade_matrix[cascade] * view_ray.world_to_light_local_matrix) *
            Matrix4x4.Scale(Vector3.one / gridResolution);

        Vector3 v1 = view_ray.world_to_light_local_matrix.MultiplyVector(view_ray.world_forward);
        v1 = v1.normalized;   /* should be a vector with two 0 and one +1/-1 component */
        v1 = v1 * 0.5f * gridResolution;
        Vector3 v2 = new Vector3(v1.y, v1.z, v1.x);
        Vector3 v3 = new Vector3(v1.z, v1.x, v1.y);
        Vector3 center = new Vector3(0.5f, 0.5f, 0.5f) * gridResolution;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(center - v1 + v2 + v3, center - v1 + v2 - v3);
        Gizmos.DrawLine(center - v1 + v2 - v3, center - v1 - v2 - v3);
        Gizmos.DrawLine(center - v1 - v2 - v3, center - v1 - v2 + v3);
        Gizmos.DrawLine(center - v1 - v2 + v3, center - v1 + v2 + v3);

        var gizmos = new List<Tuple<Vector3, Color>>();

        /* Every texel in the LightingTower 3D texture is the color of light in a small cube,
         * which is within the bounds of the camera projection.  In other words, if gridResolution
         * is 16, then the cube shown by Unity for the camera (width*height*depth) is divided in
         * 16*16*16 small cubic texels.  So when drawing the gozmos for a texel, we use
         * gizmos.DrawCube() centered at an offset "integer + dd (=0.5)".
         */
        const float dd = 0.5f;
        int index = 0;
        for (int z = 0; z < gridResolution; z++)
            for (int y = 0; y < gridResolution; y++)
                for (int x = 0; x < gridResolution; x++)
                {
                    var color = new Color(array[index], array[index + 1], array[index + 2], 1f);
                    index += 3;
                    gizmos.Add(System.Tuple.Create(
                        new Vector3(x + dd, y + dd, z + dd),
                        color));
                }

        var camera_fwd = UnityEditor.SceneView.lastActiveSceneView.camera.transform.forward;
        camera_fwd = Matrix4x4.Inverse(Gizmos.matrix).MultiplyVector(camera_fwd);
        gizmos.Sort((g1, g2) =>
        {
            var d1 = Vector3.Dot(camera_fwd, g1.Item1);
            var d2 = Vector3.Dot(camera_fwd, g2.Item1);
            return d2.CompareTo(d1);
        });
        foreach (var g in gizmos)
        {
            Gizmos.color = g.Item2 * 1.8f;
            Gizmos.DrawCube(g.Item1, Vector3.one * 0.2f);
        }
    }
#endif
}
