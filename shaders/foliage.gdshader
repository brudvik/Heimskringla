shader_type spatial;
render_mode cull_disabled;

// Erstatter de importerte StandardMaterial3D-ene på trær og busker, med vind og årstidsfarger.
// Parametrene kopieres fra originalmaterialet i PropLibrary.WindMaterial().

#include "res://shaders/wind.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_tex = false;
uniform bool use_vcol = false;
uniform bool vcol_srgb = true;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform bool use_normal = false;
uniform float alpha_cut = 0.0;       // 0 = ugjennomsiktig
uniform float roughness = 0.85;
uniform float bend = 1.0;
uniform float flutter = 1.0;
uniform float leafy = 0.0;           // 1 = løv (får årstidsfarge og gjennomskinn)
uniform float deciduous = 0.0;       // 1 = løvtre (blir gult/rødt om høsten)

global uniform vec4 season_leaf_tint;   // rgb = farge å blande mot, a = mengde (løvtrær)
global uniform float season_conifer;    // mørkere/blekere bar om vinteren (0..1)
global uniform float season_leaf_drop;    // 0 = fullt løv, 1 = bare greiner (løvtrær)
global uniform float snow_line;           // temperatur snøen går ned til (se Weather.cs)
global uniform float wetness;
global uniform sampler2D world_map : filter_linear;   // r = temperatur, g = høyde/100 (se WorldTexture)
global uniform vec4 world_rect;                      // xz-min og 1/størrelse

varying float v_hash;
varying vec3 obj_pos;
varying float v_snow;

float ihash(vec3 p) {
	uvec3 q = uvec3(ivec3(floor(p)) + ivec3(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u ^ q.z * 2654435761u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

void vertex() {
	obj_pos = VERTEX;
	vec3 root = MODEL_MATRIX[3].xyz;
	float temp = texture(world_map, (root.xz - world_rect.xy) * world_rect.zw).r;
	v_snow = smoothstep(temp - 0.03, temp + 0.03, snow_line);
	VERTEX += wind_displace(VERTEX, MODEL_MATRIX, TIME, bend, flutter);
	vec3 r = MODEL_MATRIX[3].xyz;
	v_hash = fract(sin(dot(r.xz, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec4 c = albedo_color;
	if (use_tex) c *= texture(albedo_tex, UV);
	if (use_vcol) c.rgb *= vcol_srgb ? pow(COLOR.rgb, vec3(2.2)) : COLOR.rgb;
	vec3 wn = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	if (leafy > 0.5 && deciduous > 0.5 && season_leaf_drop > 0.001) {
		// Løvfall: klumper av blader forsvinner tilfeldig
		if (ihash(obj_pos * 2.5 + v_hash * 17.0) < season_leaf_drop * 1.02) discard;
	}
	if (leafy > 0.5) {
		// Hvert tre skifter farge litt i sitt eget tempo
		float amt = clamp(season_leaf_tint.a * (0.7 + v_hash * 0.6), 0.0, 1.0) * deciduous;
		float lum = dot(c.rgb, vec3(0.3, 0.55, 0.15));
		vec3 autumn = season_leaf_tint.rgb * (0.6 + lum * 1.6) * mix(vec3(1.0), vec3(1.15, 0.8, 0.6), v_hash);
		c.rgb = mix(c.rgb, autumn, amt);
		c.rgb = mix(c.rgb, c.rgb * vec3(0.8, 0.85, 0.85), season_conifer * (1.0 - deciduous));
		BACKLIGHT = c.rgb * 0.35;
	}
	// Snø på oversiden om vinteren
	float snow = v_snow * smoothstep(0.1, 0.6, wn.y);
	c.rgb = mix(c.rgb, vec3(0.82, 0.85, 0.9), snow);
	c.rgb *= 1.0 - wetness * 0.25 * (1.0 - snow);
	ALBEDO = c.rgb;
	if (alpha_cut > 0.0) {
		ALPHA = c.a;
		ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	}
	if (use_normal) NORMAL_MAP = texture(normal_tex, UV).rgb;
	ROUGHNESS = mix(roughness, 0.35, wetness * (1.0 - snow) * 0.8);
	SPECULAR = 0.3;
}
