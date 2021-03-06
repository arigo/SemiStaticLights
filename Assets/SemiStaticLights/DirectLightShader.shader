﻿Shader "Hidden/SemiStaticLights/DirectLightShader" {
    /*
       Direct Light shader
       ===================

       Build a shadow map.  Used as shader replacement.
       We produce not just a depth map but also two extra sets of RGBA values:

         COLOR0 (precision fp16): geometrical information:
                                    R, G, B = fragment world normal
                                    A = depth again (within -0.5 and 0.5 if in range, see below)
         COLOR1 (byte precision): fragment color
    */

    Properties
    {
    }

    SubShader {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #include "UnityCG.cginc"
            #pragma vertex vert
            #pragma fragment frag

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 world_normal : TEXCOORD0;
                float2 uv_MainTex : TEXCOORD1;
            };

            struct f2a
            {
                float4 geometry : COLOR0;
                float4 color : COLOR1;
            };

            /* these come from the properties of the replaced shader */
            float4 _Color;
            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.world_normal = UnityObjectToWorldNormal(v.normal);
                o.uv_MainTex = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            f2a frag(v2f i)
            {
                float depth = i.vertex.z;
                /* if !UNITY_REVERSED_Z: 0 = near clip plane (at -127 * half_size)
                                         1 = far clip plane (at half_size)
                   Must turn it into -0.5 at -half_size, 0.5 at half_size. */
#if !defined(UNITY_REVERSED_Z)
                depth = depth * 64 - 63.5;
#else
                /* same, but starts with 1 at -127 * half_size and 0 at half_size */
                depth = 0.5 - depth * 64;
#endif
                f2a OUT;
                OUT.geometry = float4(i.world_normal, depth);
                OUT.color = tex2D(_MainTex, i.uv_MainTex) * _Color;
                return OUT;
            }
            ENDCG
        }
    }

    Fallback "VertexLit"
}
