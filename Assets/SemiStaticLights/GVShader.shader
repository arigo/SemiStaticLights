Shader "Hidden/LPVTest/GVShader" {
    /*
       Geometry Volume shader
       ======================


     */
    Properties
    {
    }

    SubShader {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            ZTest off
            ZWrite off
            Cull off
            ColorMask 0

            CGPROGRAM
            #pragma target 5.0
            #include "UnityCG.cginc"
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile   _ ORIENTATION_2 ORIENTATION_3

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                /* "centroid" changes the interpolations so that we get a value from somewhere
                   within the covered area of the pixel, instead of its center, for the case
                   where it is only partially covered.  This seems to remove the out-of-bounds
                   values causing random voxels to be marked. */
                centroid float4 vertex : SV_POSITION;
                centroid float3 world_normal : TEXCOORD0;
            };

            RWStructuredBuffer<float> _RSM_gv : register(u1);
            uint _LPV_GridResolution;
            float4x4 _LPV_WorldToLightLocalMatrix;


            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.world_normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 xyz = i.vertex.xyz;
#if defined(UNITY_REVERSED_Z)
                xyz.z = 1 - xyz.z;
#endif
                xyz.z *= _LPV_GridResolution * 0.99999;   /* makes sure 0 <= pos.z < GridResolution */

                int3 pos = int3(xyz);

                float3 normal = i.world_normal;
                normal = normalize(mul((float3x3)_LPV_WorldToLightLocalMatrix, normal));
                float reduce = normal.z;

#ifdef ORIENTATION_2
                pos = pos.yzx;
                reduce = normal.y;
#endif
#ifdef ORIENTATION_3
                pos = pos.zxy;
                reduce = normal.x;
#endif

                int index = pos.x + _LPV_GridResolution * (pos.y + _LPV_GridResolution * pos.z);
                _RSM_gv[index] = 0.0;

                /* dummy result, ignored */
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }

    Fallback "VertexLit"
}