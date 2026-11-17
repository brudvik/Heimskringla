shader_type spatial;
render_mode cull_disabled, specular_schlick_ggx;
#include "res://shaders/cloudshadow.gdshaderinc"

// Grass tufts in a MultiMesh. INSTANCE_CUSTOM.rgb = ground color where the tuft stands (sRGB), .a = random.
// UV.y = 0 at the tip, 1 at the root. COLOR.r = random per blade.

#include "res://shaders/wind.gdshaderinc"

uniform float fade_start = 38.0;
uniform float fade_end = 58.0;

global uniform float snow_line;
global uniform float wetness;
global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;
global uniform vec4 season_leaf_tint;

// Same patch noise as the terrain shader, so the grass gets the same color as the ground it stands on
float ghash(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}
float gnoise_v(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	return mix(mix(ghash(i), ghash(i + vec2(1, 0)), u.x), mix(ghash(i + vec2(0, 1)), ghash(i + vec2(1, 1)), u.x), u.y);
}

varying vec3 base_col;
varying float tip;
varying float v_hide;
varying float v_gust;
varying float v_rnd;
varying float dryness;

void vertex() {
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 root = MODEL_MATRIX[3].xyz;
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	float d = distance(root.xz, cam.xz);
	// Further out the grass is thinned randomly (tuft by tuft) instead of everything shrinking at once
	float fade_s = 1.0 - smoothstep(fade_start, fade_end, d);
	float keep = step(INSTANCE_CUSTOM.a, fade_s * 1.05 - 0.02);
	float s = keep * mix(0.7, 1.0, fade_s);
	tip = 1.0 - UV.y;
	v_rnd = COLOR.r;

	// Snow covers the grass
	float temp = texture(world_map, (root.xz - world_rect.xy) * world_rect.zw).r;
	float snow = smoothstep(temp - 0.03, temp + 0.02, snow_line);
	s *= 1.0 - snow;
	v_hide = max(snow, 1.0 - keep);

	// Wind: the whole meadow bends in the gusts (waves travelling across), each blade sways a little on its own
	float gust = wind_gust(wp.xz, TIME);
	v_gust = gust;
	float flick = sin(TIME * (2.2 + wind_strength * 3.0) + wp.x * 0.9 + wp.z * 0.7 + v_rnd * 6.28);
	float lean = wind_strength * (0.25 + gust * 1.2) + flick * (0.04 + wind_strength * 0.1);
	float t2 = tip * tip;
	vec3 dir = vec3(wind_dir.x, 0.0, wind_dir.y);
	vec3 off = dir * lean * 0.45 * t2;
	off.y = -lean * lean * 0.18 * t2;

	mat3 m = mat3(MODEL_MATRIX);
	float sc2 = dot(m[0], m[0]);
	VERTEX = VERTEX * vec3(1.0, s, 1.0) * mix(0.6, 1.0, s);
	VERTEX += transpose(m) * off * s / sc2;

	base_col = pow(INSTANCE_CUSTOM.rgb, vec3(2.2));
	// Dry/lush patches as in the terrain shader
	float big = gnoise_v(root.xz * 0.012), mid = gnoise_v(root.xz * 0.07 + 31.0);
	dryness = smoothstep(0.55, 0.85, big);
	base_col = mix(base_col, base_col * vec3(1.18, 1.08, 0.72), dryness * 0.55);
	base_col = mix(base_col, base_col * vec3(0.82, 0.95, 0.80), smoothstep(0.45, 0.15, mid) * 0.45);
	// Each tuft slightly different in color
	base_col *= mix(vec3(0.9, 1.0, 0.85), vec3(1.1, 1.02, 0.9), INSTANCE_CUSTOM.a);
	base_col = mix(base_col, base_col * vec3(1.35, 1.05, 0.55), season_leaf_tint.a * 0.6);
	base_col *= 1.0 - wetness * 0.2;
}

void fragment() {
	if (v_hide > 0.5) discard;
	// Dark at the root (dense grass shades itself), light and yellow-green towards the tip
	float ao = mix(0.6, 1.0, smoothstep(0.0, 0.6, tip));
	// Tips lighter and warmer (sunlight and flowering), not more saturated green
	vec3 tipc = base_col * vec3(1.22, 1.16, 0.88) + vec3(0.03, 0.026, 0.006);
	vec3 col = mix(base_col * 0.9, tipc, smoothstep(0.2, 1.0, tip)) * ao;
	col *= 0.9 + v_rnd * 0.2;
	// A few withered blades (straw) here and there, more where the meadow is dry
	float dry = step(0.9 - dryness * 0.25, fract(v_rnd * 13.7));
	col = mix(col, vec3(0.36, 0.3, 0.17) * ao * (0.8 + tip * 0.5), dry * 0.85);
	// Muted saturation: natural meadow, not lawn paint
	float lum = dot(col, vec3(0.3, 0.59, 0.11));
	col = mix(vec3(lum), col, 0.74);
	// Gusts brighten the grass (blades turn their undersides to the light)
	col *= 1.0 + v_gust * wind_strength * 0.25 * tip;
	// Slightly darker blade edges
	col *= 1.0 - pow(abs(UV.x - 0.5) * 2.0, 3.0) * 0.15;
	ALBEDO = col * cloud_shade(VERTEX, INV_VIEW_MATRIX);
	ROUGHNESS = mix(0.75, 0.45, wetness);
	SPECULAR = 0.25;
	BACKLIGHT = col * 0.4;
	AO = ao;
	AO_LIGHT_AFFECT = 0.4;
}
