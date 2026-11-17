shader_type spatial;
render_mode cull_disabled;
#include "res://shaders/cloudshadow.gdshaderinc"

// Replaces the imported StandardMaterial3Ds on trees and bushes, adding wind and seasonal colors.
// Parameters are copied from the original material in PropLibrary.WindMaterial().

#include "res://shaders/wind.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_tex = false;
uniform bool use_vcol = false;
uniform bool vcol_srgb = true;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform bool use_normal = false;
uniform float alpha_cut = 0.0;       // 0 = opaque
uniform float roughness = 0.85;
uniform float bend = 1.0;
uniform float flutter = 1.0;
uniform float leafy = 0.0;           // 1 = leaves (get seasonal color and translucency)
uniform float deciduous = 0.0;       // 1 = deciduous tree (turns yellow/red in autumn)
uniform float bark = 0.0;            // 1 = bark/trunk (gets bark detail, moss and a dark base)
uniform float bark_layer = 0.0;
uniform float card_fade = 0.0;       // 1 = flat cards (spruce needles): faded out when seen edge-on      // slice in the detail texture: 0 = conifer bark, 1 = deciduous bark

global uniform sampler2DArray detail_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
global uniform sampler2DArray detail_nrao : filter_linear_mipmap_anisotropic, repeat_enable;

global uniform vec4 season_leaf_tint;   // rgb = color to blend towards, a = amount (deciduous trees)
global uniform float season_conifer;    // darker/paler needles in winter (0..1)
global uniform float season_leaf_drop;    // 0 = full foliage, 1 = bare branches (deciduous trees)
global uniform float snow_line;           // temperature the snow reaches down to (see Weather.cs)
global uniform float wetness;
global uniform sampler2D world_map : filter_linear;   // r = temperature, g = height/100 (see WorldTexture)
global uniform vec4 world_rect;                      // xz min and 1/size
global uniform vec3 player_head;                     // the character's chest/head (Player.cs)

// 4x4 Bayer threshold for dithered fading (no transparency/sorting needed)
float bayer4(vec2 fc) {
	ivec2 p = ivec2(fc) & ivec2(3);
	int i = p.x + p.y * 4;
	int m[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
	return (float(m[i]) + 0.5) / 16.0;
}

// How much foliage to remove: right next to the camera, and in a "tunnel" from the camera to the character
// (so you never end up staring at a green wall when the camera ends up inside a tree crown)
float view_fade(vec3 world, vec3 cam) {
	float near = 1.0 - smoothstep(1.2, 3.0, distance(world, cam));
	vec3 seg = player_head - cam;
	float len = length(seg);
	if (len < 0.01) return near;
	float t = clamp(dot(world - cam, seg) / (len * len), 0.0, 1.0);
	float d = distance(world, cam + seg * t);
	// Only in front of the character (t < 1), and the tunnel narrows towards the camera
	float tunnel = (1.0 - smoothstep(0.9, 1.0, t)) * (1.0 - smoothstep(0.7 + t * 0.9, 1.2 + t * 1.4, d));
	return max(near, tunnel);
}
varying vec3 v_world;

varying float v_hash;
varying vec3 obj_pos;
varying float v_snow;
varying vec3 obj_n;
varying float v_h;

float ihash(vec3 p) {
	uvec3 q = uvec3(ivec3(floor(p)) + ivec3(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u ^ q.z * 2654435761u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

void vertex() {
	obj_pos = VERTEX;
	obj_n = NORMAL;
	v_h = VERTEX.y * length(MODEL_MATRIX[1].xyz);
	vec3 root = MODEL_MATRIX[3].xyz;
	float temp = texture(world_map, (root.xz - world_rect.xy) * world_rect.zw).r;
	// Seasonal snow, and always snow in the coldest zones (same threshold as the snowy ground in Biomes)
	v_snow = smoothstep(temp - 0.03, temp + 0.03, max(snow_line, 0.13));
	// Leaves: bend the normal towards a "dome" around the trunk, so the crown is lit as one soft shape instead
	// of many flat cards (the sharp light/dark patches were what looked "cut out")
	if (leafy > 0.5) {
		vec3 dome = normalize(vec3(VERTEX.x, max(VERTEX.y * 0.25, 0.0) + length(VERTEX.xz) * 0.6 + 0.2, VERTEX.z));
		NORMAL = normalize(mix(NORMAL, dome, 0.7));
	}
	VERTEX += wind_displace(VERTEX, MODEL_MATRIX, TIME, bend, flutter);
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 r = MODEL_MATRIX[3].xyz;
	v_hash = fract(sin(dot(r.xz, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec4 c = albedo_color;
	if (use_tex) c *= texture(albedo_tex, UV);
	if (use_vcol) c.rgb *= vcol_srgb ? pow(COLOR.rgb, vec3(2.2)) : COLOR.rgb;
	vec3 wn = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	if (leafy > 0.5 && !IN_SHADOW_PASS) {
		float f = view_fade(v_world, INV_VIEW_MATRIX[3].xyz);
		if (card_fade > 0.5) {
			// Card seen edge-on: texture and specular turn into a white/grey sheet; fade it out
			vec3 fn = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
			float edge = abs(dot(fn, normalize(-VERTEX)));
			f = max(f, 1.0 - smoothstep(0.08, 0.3, edge));
		}
		if (f > 0.001 && f * 1.02 > bayer4(FRAGCOORD.xy)) discard;
	}
	if (leafy > 0.5 && deciduous > 0.5 && season_leaf_drop > 0.001) {
		// Leaf fall: clumps of leaves disappear at random
		if (ihash(obj_pos * 2.5 + v_hash * 17.0) < season_leaf_drop * 1.02) discard;
	}
	if (leafy > 0.5) {
		// Each tree changes color at a slightly different pace
		float amt = clamp(season_leaf_tint.a * (0.7 + v_hash * 0.6), 0.0, 1.0) * deciduous;
		float lum = dot(c.rgb, vec3(0.3, 0.55, 0.15));
		vec3 autumn = season_leaf_tint.rgb * (0.6 + lum * 1.6) * mix(vec3(1.0), vec3(1.15, 0.8, 0.6), v_hash);
		c.rgb = mix(c.rgb, autumn, amt);
		c.rgb = mix(c.rgb, c.rgb * vec3(0.8, 0.85, 0.85), season_conifer * (1.0 - deciduous));
		// Each tree has its own tone: some fresh yellow-green, some dark blue-green, varying lightness
		vec3 tint = mix(vec3(0.84, 0.93, 0.92), vec3(1.12, 1.07, 0.82), v_hash);
		c.rgb *= tint * (0.86 + 0.26 * fract(v_hash * 7.31));
		// Conifers: darker, cooler and less saturated green (Norwegian spruce forest), not toy green
		vec3 dk = c.rgb * vec3(0.6, 0.74, 0.72);
		c.rgb = mix(c.rgb, mix(vec3(dot(dk, vec3(0.3, 0.59, 0.11))), dk, 0.8), 1.0 - deciduous);
		// Color variation per leaf clump (slightly lighter/yellower and darker/bluer areas)
		float cl = ihash(obj_pos * 1.2 + v_hash * 9.0);
		c.rgb *= mix(vec3(0.85, 0.9, 0.95), vec3(1.12, 1.08, 0.88), cl);
		// Light shines through the leaves from behind (yellow-green), and the crown is darker inside
		BACKLIGHT = c.rgb * vec3(0.9, 1.0, 0.5) * 0.8;
		float inner = clamp(length(obj_pos.xz) * 0.35, 0.0, 1.0);
		c.rgb *= mix(0.65, 1.0, inner);
	}
	// Bark: real bark detail (triplanar in object space, so it doesn't "swim" when the tree sways),
	// moss on the north side and low down, darker at the base
	if (bark > 0.5) {
		float dist = length(VERTEX);
		float near = 1.0 - smoothstep(25.0, 60.0, dist);
		vec3 on = normalize(obj_n);
		vec3 tw = pow(abs(on), vec3(4.0));
		tw /= (tw.x + tw.y + tw.z);
		float sc = length(MODEL_MATRIX[0].xyz) / 0.9;          // ~0.9 m per repeat in the world
		vec3 p = obj_pos * sc;
		if (near > 0.001) {
			vec2 ux = p.zy, uy = p.xz, uz = p.xy;
			vec3 ba = texture(detail_albedo, vec3(ux, bark_layer)).rgb * tw.x + texture(detail_albedo, vec3(uy, bark_layer)).rgb * tw.y + texture(detail_albedo, vec3(uz, bark_layer)).rgb * tw.z;
			vec4 bx = texture(detail_nrao, vec3(ux, bark_layer)), by = texture(detail_nrao, vec3(uy, bark_layer)), bz = texture(detail_nrao, vec3(uz, bark_layer));
			vec3 bmean = bark_layer < 0.5 ? vec3(0.1939, 0.135, 0.0823) : vec3(0.2548, 0.1934, 0.0825);
			// Single-color low-poly trunks (often saturated reddish-brown): pull towards real bark color
			if (!use_tex) c.rgb = mix(c.rgb, bmean * 1.2, 0.7);
			c.rgb *= mix(vec3(1.0), clamp(ba / bmean, vec3(0.3), vec3(2.2)), near * 0.85);
			vec2 nx = bx.xy * 2.0 - 1.0, ny = by.xy * 2.0 - 1.0, nz = bz.xy * 2.0 - 1.0;
			vec3 off_o = vec3(0.0, nx.y, nx.x) * tw.x + vec3(ny.x, 0.0, -ny.y) * tw.y + vec3(nz.x, nz.y, 0.0) * tw.z;
			vec3 off_v = (VIEW_MATRIX * vec4(normalize(mat3(MODEL_MATRIX) * off_o) * length(off_o), 0.0)).xyz;
			NORMAL = normalize(NORMAL + off_v * near * 0.9);
			wn = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
		}
		// Moss: north side (-Z) and low on the trunk, in patches
		float north = smoothstep(0.0, 0.7, -wn.z);
		float low = 1.0 - smoothstep(0.3, 2.5, v_h);
		float patchy = texture(detail_albedo, vec3(p.xy * 0.3 + p.z * 0.2, 2.0)).g;
		float moss = clamp(north * 0.7 + low * 0.5, 0.0, 1.0) * smoothstep(0.08, 0.2, patchy) * (1.0 - v_snow);
		vec3 mt = texture(detail_albedo, vec3(p.xz * 1.3 + p.y * 0.7, 2.0)).rgb;
		c.rgb = mix(c.rgb, mt * 1.1, moss * 0.75);
		// Darker and damper at the base
		c.rgb *= mix(0.62, 1.0, smoothstep(0.0, 1.2, v_h));
	}

	// Snow on the upper side in winter
	// Snow clumps on the upper side of branches and needles (patchy, not the whole tree white)
	float clump = ihash(obj_pos * 3.0 + v_hash * 5.0);
	float snow = v_snow * smoothstep(0.05, 0.5, wn.y + (clump - 0.5) * 0.5) * (leafy > 0.5 ? 0.85 : 1.0);
	c.rgb = mix(c.rgb, vec3(0.82, 0.85, 0.9), snow);
	c.rgb *= 1.0 - wetness * 0.25 * (1.0 - snow);
	ALBEDO = c.rgb * cloud_shade(VERTEX, INV_VIEW_MATRIX);
	if (alpha_cut > 0.0) {
		ALPHA = c.a;
		ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	}
	if (use_normal) NORMAL_MAP = texture(normal_tex, UV).rgb;
	ROUGHNESS = mix(leafy > 0.5 ? max(roughness, 0.7) : roughness, 0.35, wetness * (1.0 - snow) * 0.8);
	SPECULAR = leafy > 0.5 ? 0.15 : 0.3;
}
