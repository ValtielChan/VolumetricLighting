Shader "Hidden/VolumetricLighting"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always Cull Off ZWrite Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float  _VL_Intensity;
        float  _VL_Steps;
        float  _VL_MaxDistance;
        float  _VL_Scattering;
        float  _VL_Density;
        float4 _VL_Tint;

        #define VL_MAX_SPOTS 8
        int    _VL_SpotCount;
        float4 _VL_SpotPos[VL_MAX_SPOTS];   // xyz = world pos, w = 1 / range
        float4 _VL_SpotDir[VL_MAX_SPOTS];   // xyz = forward,   w = cosOuter
        float4 _VL_SpotColor[VL_MAX_SPOTS]; // rgb = color*intensity, w = cosInner

        // Henyey-Greenstein phase function for forward-scattering god rays.
        float HenyeyGreenstein(float cosTheta, float g)
        {
            float g2 = g * g;
            float denom = 1.0 + g2 - 2.0 * g * cosTheta;
            return (1.0 - g2) / (4.0 * PI * pow(max(denom, 1e-4), 1.5));
        }

        // Interleaved Gradient Noise - cheap and good enough for hiding banding.
        float IGN(float2 pixelPos)
        {
            return frac(52.9829189 * frac(dot(pixelPos, float2(0.06711056, 0.00583715))));
        }

        // Sample the directional shadow map directly (bypasses screen-space shadow path,
        // which doesn't make sense for points in mid-air).
        float SampleSunShadow(float3 positionWS)
        {
        #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
            half cascadeIndex = ComputeCascadeIndex(positionWS);
            float4 shadowCoord = mul(_MainLightWorldToShadow[cascadeIndex], float4(positionWS, 1.0));
            ShadowSamplingData samplingData = GetMainLightShadowSamplingData();
            half4 shadowParams = GetMainLightShadowParams();
            return SampleShadowmap(TEXTURE2D_ARGS(_MainLightShadowmapTexture, sampler_LinearClampCompare),
                                   shadowCoord, samplingData, shadowParams, false);
        #else
            return 1.0;
        #endif
        }

        // Accumulated in-scattering from all registered spot lights at world position p.
        // rayDir is the camera->sample direction (used for the phase function).
        float3 AccumulateSpots(float3 p, float3 rayDir, float g, float sigmaS)
        {
        #if defined(_VL_SPOTS_ON)
            float3 sum = 0;
            int count = min(_VL_SpotCount, VL_MAX_SPOTS);
            [loop]
            for (int s = 0; s < count; s++)
            {
                float3 spotPos   = _VL_SpotPos[s].xyz;
                float  invRange  = _VL_SpotPos[s].w;
                float3 spotFwd   = _VL_SpotDir[s].xyz;
                float  cosOuter  = _VL_SpotDir[s].w;
                float3 spotCol   = _VL_SpotColor[s].rgb;
                float  cosInner  = _VL_SpotColor[s].w;

                float3 toLight = spotPos - p;
                float dist2 = dot(toLight, toLight);
                float dist  = sqrt(max(dist2, 1e-8));
                float3 L = toLight / dist; // from sample toward light

                // Cheap rejection: out of range.
                if (dist * invRange >= 1.0) continue;

                // Distance attenuation matching URP's smooth-windowed inverse-square.
                float distRange = dist * invRange;
                float window = saturate(1.0 - distRange * distRange * distRange * distRange);
                window *= window;
                // Treat the spot as a small sphere rather than a point: an unclamped
                // 1/d^2 explodes where the ray passes next to the bulb and the raymarch
                // jitter turns that spike into visible dither.
                float distAtt = window / max(dist2, 0.25);

                // Spot cone: angle between light-forward and the vector from light to sample.
                float cosAng = dot(-L, spotFwd);
                if (cosAng <= cosOuter) continue;

                float coneAtt = saturate((cosAng - cosOuter) / max(cosInner - cosOuter, 1e-4));
                coneAtt *= coneAtt; // soften edge

                float att = distAtt * coneAtt;
                float phase = HenyeyGreenstein(dot(rayDir, L), g);
                sum += spotCol * phase * att * sigmaS;
            }
            return sum;
        #else
            return 0;
        #endif
        }

        float4 FragRaymarch(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;

            float rawDepth = SampleSceneDepth(uv);

            // Treat skybox (far plane) as a fixed depth so we still get rays in empty sky.
            #if UNITY_REVERSED_Z
                bool isSky = rawDepth <= 1e-6;
            #else
                bool isSky = rawDepth >= 1.0 - 1e-6;
            #endif

            float3 worldPosScene = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
            float3 camPos = _WorldSpaceCameraPos;

            float3 toScene = worldPosScene - camPos;
            float sceneDist = isSky ? _VL_MaxDistance : length(toScene);
            float3 rayDir = isSky
                ? normalize(ComputeWorldSpacePosition(uv, UNITY_RAW_FAR_CLIP_VALUE, UNITY_MATRIX_I_VP) - camPos)
                : (toScene / max(sceneDist, 1e-4));

            float marchLen = min(sceneDist, _VL_MaxDistance);
            int steps = (int)_VL_Steps;
            float stepSize = marchLen / max((float)steps, 1.0);

            float jitter = IGN(input.positionCS.xy);

            Light mainLight = GetMainLight();
            float3 lightDir   = mainLight.direction;
            float3 lightColor = mainLight.color;

            float phase = HenyeyGreenstein(dot(rayDir, lightDir), _VL_Scattering);

            // Slider 0..5 maps to "per-meter" scattering coefficient.
            float sigmaS = _VL_Density * 0.01;
            float sigmaE = sigmaS;

            // Skip the directional contribution entirely when the sun is dark — saves
            // the shadow sample inside the loop. Spots still contribute via AccumulateSpots.
            bool hasSun = dot(lightColor, float3(1,1,1)) > 1e-4 && _VL_Intensity > 0.0;

            float3 accum = 0;
            float transmittance = 1.0;

            [loop]
            for (int i = 0; i < steps; i++)
            {
                float t = (i + jitter) * stepSize;
                if (t > marchLen) break;

                float3 p = camPos + rayDir * t;

                float3 scatter = 0;
                if (hasSun)
                {
                    float shadow = SampleSunShadow(p);
                    scatter += lightColor * phase * shadow * sigmaS * _VL_Intensity;
                }
                scatter += AccumulateSpots(p, rayDir, _VL_Scattering, sigmaS);
                accum += scatter * transmittance * stepSize;

                transmittance *= exp(-sigmaE * stepSize);
                if (transmittance < 0.01) break;
            }

            // Tint applies to both directional and spot scattering.
            // Spot contributions are already scaled by per-light intensity * global spotIntensity on the CPU.
            return float4(accum * _VL_Tint.rgb, 1.0);
        }

        // Pass 2 just samples the half-res volumetric and outputs it; the pass uses
        // hardware additive blending (Blend One One) to composite into camera color.
        float4 FragComposite(Varyings input) : SV_Target
        {
            return float4(SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb, 1.0);
        }
        ENDHLSL

        Pass
        {
            Name "VolumetricRaymarch"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRaymarch
            #pragma target 3.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _VL_SPOTS_ON
            ENDHLSL
        }

        Pass
        {
            Name "VolumetricComposite"
            Blend One One
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 3.5
            ENDHLSL
        }
    }
    Fallback Off
}
