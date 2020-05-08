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
    public int drawGizmosLevelMin = 0;
    public int drawGizmosLevelCount = 99;


    Vector3 _light_center;
    Camera _shadowCam;
    Matrix4x4[] _world_to_light_local_matrix;
    RenderTexture[] _tex3d_gvs = Array.Empty<RenderTexture>();


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

        if (_tex3d_gvs.Length != numCascades || _tex3d_gvs[0].width != gridResolution)
        {
            DestroyTargets();
            _tex3d_gvs = new RenderTexture[numCascades];
            for (int i = 0; i < numCascades; i++)
                _tex3d_gvs[i] = CreateTex3dGV();
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
            //DirectionalCopyGV(orientation);
        }
        DirectionalCopyGV(2);
    }

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

    void RenderGV()
    {
        var cam = FetchShadowCamera();

        _world_to_light_local_matrix = new Matrix4x4[_tex3d_gvs.Length];

        for (int i = 0; i < _tex3d_gvs.Length; i++)
        {
            float pixel_size = gridPixelSize * Mathf.Pow(2f, i);
            float half_size = 0.5f * gridResolution * pixel_size;
            var mat = Matrix4x4.Scale(Vector3.one / (2f * half_size)) *
                      Matrix4x4.Translate(Vector3.one * half_size) *
                      cam.transform.worldToLocalMatrix;
            _world_to_light_local_matrix[i] = mat;

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
            cam.transform.position -= 0.5f * pixel_size * (axis_x + axis_y);
            cam.RenderWithShader(gvShader, "RenderType");
            cam.transform.SetPositionAndRotation(orig_position, orig_rotation);

            cam.transform.Rotate(axis_y, -90, Space.World);
            cam.transform.Rotate(axis_z, -90, Space.World);
            cam.transform.position -= 0.5f * pixel_size * (axis_z + axis_x);
            Shader.EnableKeyword("ORIENTATION_2");
            cam.RenderWithShader(gvShader, "RenderType");
            Shader.DisableKeyword("ORIENTATION_2");
            cam.transform.SetPositionAndRotation(orig_position, orig_rotation);

            cam.transform.Rotate(axis_z, 90, Space.World);
            cam.transform.Rotate(axis_y, 90, Space.World);
            cam.transform.position -= 0.5f * pixel_size * (axis_y + axis_z);
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

        int directional_copy_kernel = gvCompute.FindKernel("DirectionalCopy");
        gvCompute.SetInts("DX", (int)DX.x, (int)DX.y, (int)DX.z);
        gvCompute.SetInts("DY", (int)DY.x, (int)DY.y, (int)DY.z);
        gvCompute.SetInts("DZ", (int)DZ.x, (int)DZ.y, (int)DZ.z);

        for (int i = 1; i < _tex3d_gvs.Length; i++)
        {
            gvCompute.SetTexture(directional_copy_kernel, "Input_gv", _tex3d_gvs[i - 1]);
            gvCompute.SetTexture(directional_copy_kernel, "LPV_gv", _tex3d_gvs[i]);
            int thread_groups = (gridResolution + 7) / 8;
            gvCompute.Dispatch(directional_copy_kernel, thread_groups, thread_groups, thread_groups);
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
            for (int i = 0; i < _tex3d_gvs.Length; i++)
            {
                if (i < drawGizmosLevelMin)
                    continue;
                if (i >= drawGizmosLevelMin + drawGizmosLevelCount)
                    continue;
                DrawGizmosGV(_tex3d_gvs[i], _world_to_light_local_matrix[i]);
            }
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

        const float dd = 0.0f;
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
#endif
}
