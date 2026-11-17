shader_type spatial;
#include "res://shaders/cloudshadow.gdshaderinc"

// Birch bark (used on the CommonTree models to make birches):
//  - white, papery bark with dark horizontal pore streaks (lenticels) and peeled, pinkish-brown patches
//  - dark, deeply furrowed bark at the base (real bark texture Bark014) blending into white with a ragged edge
//  - fine wood structure (Bark012 normal) over the whole trunk, moss low on the north side
// Textures are sampled in object space (they follow the tree as it sways).

#include "res://shaders/wind.gdshaderinc"

uniform float bend = 1.0;
uniform float flutter = 1.0;
uniform sampler2D bark_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D bark_normal : hint_normal, filter_linear_mipmap, repeat_enable;

global uniform sampler2DArray detail_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
global uniform sampler2DArray detail_nrao : filter_linear_mipmap_anisotropic, repeat_enable;
global uniform float snow_line;
global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;

varying vec3 obj_pos;
varying vec3 obj_n;
varying float v_h;
varying float v_snow;

float hash(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex() {
	obj_pos = VERTEX;
	obj_n = NORMAL;
	v_h = VERTEX.y * length(MODEL_MATRIX[1].xyz);
	vec3 root = MODEL_MATRIX[3].xyz;
	float temp = texture(world_map, (root.xz - world_rect.xy) * world_rect.zw).r;
	v_snow = smoothstep(temp - 0.03, temp + 0.03, snow_line);
	VERTEX += wind_displace(VERTEX, MODEL_MATRIX, TIME, bend, flutter);
}

void fragment() {
	float sc = length(MODEL_MATRIX[0].xyz);
	vec3 p = obj_pos * sc;                                  // meters, in the tree's own space
	float ang = atan(obj_pos.z, obj_pos.x);
	float circ = ang * length(obj_pos.xz) * sc;             // meters around the trunk
	vec2 cyl = vec2(circ, p.y);                             // cylinder coordinates for the streaks

	// White bark with faint grey tones
	float lum = dot(texture(bark_tex, UV).rgb, vec3(0.333));
	vec3 col = vec3(0.74, 0.73, 0.69) * (0.85 + lum * 0.25);
	// Grey blotches and faint vertical veins in the white bark
	col *= 0.86 + vnoise(cyl * vec2(4.0, 1.5)) * 0.2;
	col *= 0.94 + vnoise(cyl * vec2(30.0, 2.0)) * 0.08;
	col = mix(col, col * vec3(0.78, 0.78, 0.76), smoothstep(0.6, 0.8, vnoise(cyl * vec2(2.0, 0.6) + 7.0)) * 0.6);
	// Peeled patches (light pinkish-brown where the outer layer has come off)
	float peel = smoothstep(0.72, 0.8, vnoise(cyl * vec2(5.0, 2.2) + 11.0));
	col = mix(col, vec3(0.78, 0.62, 0.52), peel * 0.6);
	// Lenticels: short, horizontal dark streaks
	float lx = vnoise(vec2(cyl.x * 6.0, cyl.y * 30.0));
	float len = vnoise(vec2(cyl.x * 14.0, cyl.y * 30.0) + 5.0);
	float lent = smoothstep(0.68, 0.78, lx) * smoothstep(0.3, 0.55, len);
	// Fine, short pores as well
	lent = max(lent, smoothstep(0.8, 0.88, vnoise(vec2(cyl.x * 25.0, cyl.y * 70.0))) * 0.6);
	// Larger black "eyes" around branch scars
	float eye = smoothstep(0.82, 0.9, vnoise(cyl * vec2(1.4, 0.7) + 3.0));
	col = mix(col, vec3(0.1, 0.09, 0.085), max(lent * 0.85, eye));

	// Dark, furrowed base: real bark texture, ragged transition
	float base_line = 0.6 + vnoise(vec2(circ * 2.0, 0.0)) * 1.0;
	float base = 1.0 - smoothstep(base_line - 0.35, base_line + 0.1, v_h);
	vec3 on = normalize(obj_n);
	vec3 tw = pow(abs(on), vec3(4.0));
	tw /= (tw.x + tw.y + tw.z);
	vec3 q = p / 0.7;
	vec3 dark = (texture(detail_albedo, vec3(q.zy, 0.0)).rgb * tw.x + texture(detail_albedo, vec3(q.xz, 0.0)).rgb * tw.y
	           + texture(detail_albedo, vec3(q.xy, 0.0)).rgb * tw.z) * 0.6;
	col = mix(col, dark, base);

	// Fine structure: Bark012 normal (weak on the smooth white part, strong at the base)
	vec3 r = p / 0.5;
	vec4 nx = texture(detail_nrao, vec3(r.zy, 1.0)), ny = texture(detail_nrao, vec3(r.xz, 1.0)), nz = texture(detail_nrao, vec3(r.xy, 1.0));
	vec2 ax = nx.xy * 2.0 - 1.0, ay = ny.xy * 2.0 - 1.0, az = nz.xy * 2.0 - 1.0;
	vec3 off_o = vec3(0.0, ax.y, ax.x) * tw.x + vec3(ay.x, 0.0, -ay.y) * tw.y + vec3(az.x, az.y, 0.0) * tw.z;
	vec3 off_v = (VIEW_MATRIX * vec4(mat3(MODEL_MATRIX) * off_o / max(sc, 0.001), 0.0)).xyz;
	float dist = length(VERTEX);
	float near = 1.0 - smoothstep(25.0, 60.0, dist);
	NORMAL = normalize(NORMAL + off_v * near * mix(0.45, 1.1, base));
	vec3 wn = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;

	// Moss low on the north side
	float north = smoothstep(0.0, 0.7, -wn.z);
	float moss = north * (1.0 - smoothstep(0.2, 1.4, v_h)) * smoothstep(0.4, 0.6, vnoise(p.xy * 3.0 + p.z));
	col = mix(col, texture(detail_albedo, vec3(p.xz * 1.3 + p.y * 0.7, 2.0)).rgb * 1.1, moss * 0.7 * (1.0 - v_snow));
	// Snow on the upper side of branches in winter
	col = mix(col, vec3(0.84, 0.87, 0.92), v_snow * smoothstep(0.2, 0.6, wn.y));

	ALBEDO = col * cloud_shade(VERTEX, INV_VIEW_MATRIX);
	ROUGHNESS = mix(0.7, 0.92, base);
}
