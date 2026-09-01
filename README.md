# Volumetric Lighting

Raymarched volumetric scattering for the Universal Render Pipeline. Real god
rays through the main directional light's shadow cascades, plus opt-in
scattering from spot lights. One renderer feature, one Volume override, no
per-object setup — drop it in and your existing lights start casting light
through the air.

![Sun shafts through a slotted wall, with three coloured volumetric spot lights](Documentation/images/hero.png)

<p align="center">
  <img src="Documentation/images/shafts-angle.png" width="49%" alt="Shafts seen from inside the colonnade">
  <img src="Documentation/images/denoise-on.png" width="49%" alt="Volumetric spot lights at night, sun disabled">
</p>

<sub>The demo scene (`Samples/Demo.unity`) at 1600×900, Full resolution,
128 raymarch steps, 1 blur iteration. Right: the same scene with the sun
turned off — only the three `VolumetricSpotLight` cones remain.</sub>

## Blur off / on

<p align="center">
  <img src="Documentation/images/denoise-off.png" width="49%" alt="Blur Iterations 0 - the raymarch jitter shows as dither">
  <img src="Documentation/images/denoise-on.png" width="49%" alt="Blur Iterations 1 - smooth cones">
</p>

<sub>`Blur Iterations` 0 and 1 on the renderer feature. The raymarch offsets each
pixel's samples by a noise pattern to trade banding for noise; without a filter
that noise is what you see. One separable gaussian over the volumetric buffer
resolves it — see [The dither, and how to get rid of it](#the-dither-and-how-to-get-rid-of-it).</sub>

## Same frame, effect off / on

<p align="center">
  <img src="Documentation/images/compare-off.png" width="49%" alt="Volume override disabled">
  <img src="Documentation/images/hero.png" width="49%" alt="Volume override enabled">
</p>

<sub>Nothing changes but the `Enabled` checkbox on the Volumetric Lighting
override. Geometry, materials and light setup are identical.</sub>

## Requirements

- Unity **6000.0.61f1** (Unity 6.0 LTS) or later — the version this was
  developed and tested on.
- Universal Render Pipeline **17+**, using **Render Graph** (the default in
  Unity 6). The pass is written against the Render Graph API only.

## Install

Copy the `Volumetric Lighting` folder into your project's `Assets/`. That's
the whole asset — two assembly definitions, one shader, no dependencies
beyond URP itself.

## Setup

1. Open your **URP Renderer** asset (`Assets/Settings/*_Renderer.asset` in a
   default project) and `Add Renderer Feature → Volumetric Lighting Feature`.
2. Add a **Volume** to your scene (or reuse an existing one) and
   `Add Override → Lighting → Volumetric Lighting`.
3. Tick **Enabled** on the override, and raise **Density** until you see it.
4. Optional: add `Add Component → Rendering → Volumetric Spot Light` to each
   spot light that should scatter.

`Samples/Demo.unity` is steps 2–4 already done; you still need step 1, since
renderer features live on the render pipeline asset rather than in the scene.

## Volume parameters

| Parameter | What it does |
|---|---|
| `Enabled` | Master switch. Off = the pass is never enqueued, zero cost. |
| `Intensity` | Scattering multiplier for the **main directional light**. `0` skips the sun path (and its shadow sample) entirely — set it to 0 at night. |
| `Steps` | Raymarch samples per pixel. **16–24** mobile, **32–48** desktop. |
| `Max Distance` | How far the ray marches, in meters. Keep it near the size of what the camera actually sees: a large value spreads the same step count over more distance and coarsens the result. |
| `Scattering` | Henyey-Greenstein anisotropy. Positive = forward scattering, the classic bright halo around the sun. |
| `Density` | Scattering coefficient of the medium — "how much fog". |
| `Tint` | HDR tint applied to every contribution. |
| `Spots Enabled` / `Spot Intensity` | Enable and globally scale the spot light contribution. |
| `Spot Cull Distance` | Spot lights further than this from the camera are skipped on the CPU. `0` = no culling. |

The demo profile (`Samples/VolumetricLightingProfile.asset`) is a reasonable
desktop starting point.

## Renderer feature settings

| Setting | What it does |
|---|---|
| `Injection Point` | When the pass runs. Default `BeforeRenderingTransparents`, so transparents composite over the fog. |
| `Resolution` | Raymarch buffer size: `Full`, `Half`, `Quarter`. **Quarter** is the mobile default, `Full` for stills and desktop. |
| `Blur Iterations` | Separable gaussian passes over the volumetric buffer before compositing. **1** removes the raymarch dither and is the default; **2** for very bright spot cones; **0** skips the blur entirely (two passes cheaper, and the dither comes back). |
| `Shader` | Auto-resolved to `Hidden/VolumetricLighting`; leave it empty. |

### Presets that work

| | Resolution | Blur Iterations | Steps (Volume) |
|---|---|---|---|
| **Mobile** | Quarter | 1 | 16–24 |
| **Balanced** | Half | 1 | 32–48 |
| **PC / stills** | Full | 1–2 | 64–128 |

## How it works

Recorded on the Render Graph:

1. **Raymarch** into an off-screen `R16G16B16A16_SFloat` target at the chosen
   resolution. For each pixel the scene depth gives the ray's end point (the
   sky clamps to `Max Distance`), and the ray is marched with an
   interleaved-gradient-noise jitter to break up banding. At each step the
   directional light's **cascade shadow map is sampled directly** — not the
   screen-space shadow texture, which is meaningless for a point floating in
   mid-air — and every registered spot light is accumulated analytically.
   The loop early-outs once transmittance drops below 1%.
2. **Blur**, `Blur Iterations` times: a 9-tap gaussian folded into 5 bilinear
   fetches, horizontal then vertical, over the volumetric buffer only.
3. **Composite** the result into camera colour with hardware additive
   blending (`Blend One One`) — no extra fullscreen copy.

Spot lights are packed CPU-side into three `Vector4` arrays (position +
1/range, forward + cos(outer), colour·intensity + cos(inner)) and culled by
camera distance there; the shader culls again per sample by range and cone
angle. The `_VL_SPOTS_ON` keyword removes the whole spot loop when no spot is
active.

The pass sets `requiresIntermediateTexture`, which forces URP to allocate an
intermediate colour target. Without it URP renders straight to the back
buffer in simple frames and the composite — which reads a texture while
writing camera colour — silently does nothing.

## The dither, and how to get rid of it

Marching a fixed number of steps through a light shaft quantises it into
bands. The standard cure is to offset each pixel's first sample by a noise
pattern, which is what the raymarch does — that trades the bands for
per-pixel noise, on the assumption that something downstream will resolve
the noise. With nothing downstream, you see the noise directly, and because
the pattern is interleaved gradient noise it reads as a regular dither
rather than as grain.

It shows up **where a step is too coarse for how fast the light changes over
it**, which is why it lands on spot cones and not on broad sun shafts: close
to the bulb, the whole cone profile can be narrower than a single step.

Two knobs, and they work together:

- **`Blur Iterations` (renderer feature)** — one separable gaussian over the
  volumetric buffer. This is the cheap fix and it is on by default. Because
  the buffer is off-screen and often at half or quarter resolution, the blur
  is nearly free, and it only ever touches the fog: geometry, edges and the
  rest of the frame are untouched.
- **`Steps` (Volume)** — the actual fix, since the dither is an undersampling
  artifact. Raising `Steps`, or lowering `Max Distance` so the same steps
  cover less ground, attacks the cause rather than the symptom.

At `Blur Iterations` 1 the demo scene is clean at 64 steps everywhere except
the last metre before each bulb; at 128 steps it is clean everywhere. On
mobile, Quarter resolution with 1 iteration and 16–24 steps stays smooth
because the low-resolution buffer is itself a filter.

Widening happens by **repeating** the kernel, not by stretching its taps:
spreading a 5-tap gaussian over more texels undersamples the kernel and
aliases the dither into a coarser, more visible pattern.

## Mobile guidance

- Renderer feature resolution → **Quarter**.
- `Steps` 16–24, `Density` 0.1–0.3, `Max Distance` 30–40 m.
- Keep 2–4 `VolumetricSpotLight` visible at once.
- Set `Intensity` to 0 whenever the sun is not the story: it removes the
  shadow-map sample from the inner loop, which is the expensive part.

## Limits

- **No shadows for spot lights.** Headlights and candles scatter through
  walls; only the main directional light is occluded.
- **No light cookies** on the volumetrics.
- **8 active spot lights** maximum. Raise `VolumetricSpotLight.MaxActive` and
  `VL_MAX_SPOTS` in the shader together if you need more.
- **Point lights are not supported** — directional and spot only.
- **No temporal reprojection and no bilateral upsample.** The blur is a plain
  gaussian, so at Half or Quarter resolution the fog can bleed a pixel or two
  past a hard geometry edge, and very bright thin cones want `Blur Iterations`
  2 or a higher `Steps` rather than the default.

## License

[MIT](LICENSE.md) — free to use, modify and redistribute, including in
commercial projects. Just keep the copyright notice and license text.

© 2026 Fabien Boco
