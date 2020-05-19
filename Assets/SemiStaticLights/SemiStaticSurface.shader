Shader "SemiStaticLights/SemiStaticSurface"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

//        CGPROGRAM
//        // Physically based Standard lighting model, and enable shadows on all light types
//        #pragma surface surf Standard fullforwardshadows
//
//        // Use shader model 3.0 target, to get nicer looking lighting
//        #pragma target 3.0
//
//        sampler2D _MainTex;
//
//        struct Input
//        {
//            float2 uv_MainTex;
//        };
//
//        half _Glossiness;
//        half _Metallic;
//        fixed4 _Color;
//
//        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
//        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
//        // #pragma instancing_options assumeuniformscaling
//        UNITY_INSTANCING_BUFFER_START(Props)
//            // put more per-instance properties here
//        UNITY_INSTANCING_BUFFER_END(Props)
//
//        void surf (Input IN, inout SurfaceOutputStandard o)
//        {
//            // Albedo comes from a texture tinted by color
//            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
//            o.Albedo = c.rgb;
//            // Metallic and smoothness come from slider variables
//            o.Metallic = _Metallic;
//            o.Smoothness = _Glossiness;
//            o.Alpha = c.a;
//        }
//        ENDCG



//        Pass
//        {
//            CGPROGRAM
//            #pragma target 5.0
//            #include "UnityCG.cginc"
//            #pragma vertex vert
//            #pragma fragment frag
//
//            struct appdata
//            {
//                float4 vertex : POSITION;
//            };
//
//            struct v2f
//            {
//                float4 vertex : SV_POSITION;
//                float3 light_uvw : TEXCOORD0;
//            };
//
//            sampler3D _LPV_LightingTower;
//            float4 _LPV_ShowCascade;
//            float4x4 _LPV_WorldToLightLocalMatrix;
//
//
//            v2f vert(appdata v)
//            {
//                v2f o;
//                o.vertex = UnityObjectToClipPos(v.vertex);
//
//                float4 world4 = mul(unity_ObjectToWorld, v.vertex);
//                float4 lightlocal4 = mul(_LPV_WorldToLightLocalMatrix, world4);
//                o.light_uvw = lightlocal4.xyz / lightlocal4.w;
//
//                return o;
//            }
//
//            fixed4 frag(v2f i) : SV_Target
//            {
//                float3 light_uvw = i.light_uvw * _LPV_ShowCascade.xyz;
//                light_uvw.z += _LPV_ShowCascade.w;
//                float4 light_color = tex3D(_LPV_LightingTower, light_uvw);
//                light_color.a = 1;
//                return light_color;
//            }
//            ENDCG
//        }

        Pass
        {
            CGPROGRAM
            #pragma target 5.0
            #include "UnityCG.cginc"
            #pragma vertex vert
            #pragma fragment frag
#include "Assets/ShaderDebugger/debugger.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 light_uvw : TEXCOORD0;
                float3 light_normal : TEXCOORD1;
                nointerpolation int ray_index : TEXCOORD2;
            };

            float4x4 _LPV_WorldToLightLocalMatrix;
            sampler3D _SSL_LightingTower;
            float4 _SSL_CascadeStuff;
            float _SSL_GridResolutionExtra;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);

                float4 world4 = mul(unity_ObjectToWorld, v.vertex);
                float4 lightlocal4 = mul(_LPV_WorldToLightLocalMatrix, world4);
                o.light_uvw = lightlocal4.xyz / lightlocal4.w;

                float3 world_normal = mul((float3x3)unity_ObjectToWorld, v.normal);
                float3 light_normal = mul((float3x3)_LPV_WorldToLightLocalMatrix, world_normal);
                o.light_normal = light_normal;

                float3 test = abs(light_normal);
                float cascade;
                if (test.x >= max(test.y, test.z))
                    cascade = light_normal.x < 0 ? 0 : 1;
                else if (test.y >= max(test.x, test.z))
                    cascade = light_normal.y < 0 ? 2 : 3;
                else
                    cascade = light_normal.z < 0 ? 4 : 5;
                o.ray_index = cascade;

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                /* 'uvw' is three coordinates between -0.5 and 0.5 if we're inside the level-0
                   cascade.  The value of _SSL_CascadeStuff.w is computed by the C# code. */
                float3 uvw = i.light_uvw;

                float3 uvw_abs = abs(uvw);
                float magnitude = max(max(uvw_abs.x, uvw_abs.y), uvw_abs.z);
                float cascade = floor(log2(max(magnitude * /*_SSL_CascadeStuff.w*/ 4.5, 1)));
                /* ^^^ an integer at least 0 */
                float inv_cascade_scale = exp2(-cascade);
                uvw *= inv_cascade_scale;


                uvw += normalize(i.light_normal) / 16;
                /*switch (i.ray_index)
                {
                case 0: uvw.x -= _SSL_GridResolutionExtra; break;
                case 1: uvw.x += _SSL_GridResolutionExtra; break;
                case 2: uvw.y -= _SSL_GridResolutionExtra; break;
                case 3: uvw.y += _SSL_GridResolutionExtra; break;
                case 4: uvw.z -= _SSL_GridResolutionExtra; break;
                case 5: uvw.z += _SSL_GridResolutionExtra; break;
                }*/

                uint root = DebugFragment(i.vertex);
                DbgValue1(root, cascade);


                uvw += float3(0.5 + i.ray_index, 0.5, 0.5 + cascade);
                uvw *= _SSL_CascadeStuff.xyz;    /* 1/18, 1, 1/numCascades */

                float3 light_color = tex3D(_SSL_LightingTower, uvw).rgb;
                light_color *= 3;
                return float4(light_color, 1);
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
