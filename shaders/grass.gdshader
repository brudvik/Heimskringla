shader_type spatial;
render_mode cull_disabled;

// Gresstuster i MultiMesh. INSTANCE_CUSTOM.rgb = bakkefargen der tusten står (sRGB).
// UV.y = 0 i toppen, 1 ved roten.

#include "res://shaders/wind.gdshaderinc"

uniform float fade_start = 45.0;
uniform float fade_end = 65.0;

global uniform float snow_line;
global uniform float wetness;
global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;
global uniform vec4 season_leaf_tint;

varying vec3 base_col;
varying float tip;
varying float v_hide;

void vertex() {
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	float d = distance(wp.xz, cam.xz);
	float s = 1.0 - smoothstep(fade_start, fade_end, d);
	tip = 1.0 - UV.y;
	vec2 wd = wind_dir;
	float gust = wind_gust(wp.xz, TIME);
	float sway = sin(TIME * (1.7 + wind_strength * 2.0) + wp.x * 0.35 + wp.z * 0.27) * 0.5 + sin(TIME * 3.1 + wp.x * 1.3) * 0.15;
	// Snø dekker gresset (krymper), høsten gjør det gulere
	float temp = texture(world_map, (wp.xz - world_rect.xy) * world_rect.zw).r;
	float snow = smoothstep(temp - 0.03, temp + 0.02, snow_line);
	s *= 1.0 - snow;
	v_hide = snow;
	VERTEX.y *= s;
	// Bøy i vindretningen (lent) + vugging; MODEL_MATRIX er tustens rotasjon, så gå via objektrom
	float lean = (0.15 + wind_strength * (0.4 + gust * 1.1)) * tip;
	vec3 off = vec3(wd.x, 0.0, wd.y) * lean * 0.35 + vec3(wd.x, 0.0, wd.y) * sway * (0.05 + wind_strength * 0.12) * tip;
	off.y = -lean * lean * 0.12;
	mat3 m = mat3(MODEL_MATRIX);
	VERTEX += transpose(m) * off * s / dot(m[0], m[0]);
	base_col = pow(INSTANCE_CUSTOM.rgb, vec3(2.2));
	base_col = mix(base_col, base_col * vec3(1.35, 1.05, 0.55), season_leaf_tint.a * 0.6);
	base_col *= 1.0 - wetness * 0.2;
}

void fragment() {
	if (v_hide > 0.5) discard;
	vec3 col = mix(base_col * 0.75, base_col * 1.35 + vec3(0.03, 0.04, 0.0), tip);
	ALBEDO = col;
	ROUGHNESS = 0.9;
	SPECULAR = 0.1;
	BACKLIGHT = vec3(0.15, 0.2, 0.05);
}
