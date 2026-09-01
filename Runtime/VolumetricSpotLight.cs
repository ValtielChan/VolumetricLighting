using System.Collections.Generic;
using UnityEngine;

namespace VolumetricLighting
{
    [RequireComponent(typeof(Light))]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("Rendering/Volumetric Spot Light")]
    public sealed class VolumetricSpotLight : MonoBehaviour
    {
        public const int MaxActive = 8;

        [Tooltip("Per-light multiplier applied on top of the global spot intensity.")]
        [Min(0f)] public float volumetricIntensity = 1f;

        [Tooltip("Optional override of the volumetric reach in meters. 0 = use Light.range.")]
        [Min(0f)] public float rangeOverride = 0f;

        private static readonly List<VolumetricSpotLight> s_Active = new(MaxActive);
        public static IReadOnlyList<VolumetricSpotLight> Active => s_Active;

        private Light _light;
        public Light Light => _light != null ? _light : (_light = GetComponent<Light>());

        private void OnEnable()
        {
            if (!s_Active.Contains(this)) s_Active.Add(this);
        }

        private void OnDisable()
        {
            s_Active.Remove(this);
        }
    }
}
