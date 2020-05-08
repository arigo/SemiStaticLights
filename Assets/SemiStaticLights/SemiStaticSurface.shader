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

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        sampler2D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
        };

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Albedo comes from a texture tinted by color
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            // Metallic and smoothness come from slider variables
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG



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
    }
    FallBack "Diffuse"
}
