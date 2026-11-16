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
uniform float bark = 0.0;            // 1 = bark/stamme (får barkdetalj, mose og mørk rot)
uniform float bark_layer = 0.0;      // skive i detail-teksturen: 0 = bartrebark, 1 = løvtrebark

global uniform sampler2DArray detail_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
global uniform sampler2DArray detail_nrao : filter_linear_mipmap_anisotropic, repeat_enable;

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
	// Årstidssnø, og alltid snø i de kaldeste sonene (samme grense som snøbakken i Biomes)
	v_snow = smoothstep(temp - 0.03, temp + 0.03, max(snow_line, 0.13));
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
	// Bark: ekte barkdetalj (triplanar i objektrom, så den ikke "flyter" når treet svaier),
	// mose på nordsiden og nederst, mørkere ved roten
	if (bark > 0.5) {
		float dist = length(VERTEX);
		float near = 1.0 - smoothstep(25.0, 60.0, dist);
		vec3 on = normalize(obj_n);
		vec3 tw = pow(abs(on), vec3(4.0));
		tw /= (tw.x + tw.y + tw.z);
		float sc = length(MODEL_MATRIX[0].xyz) / 0.9;          // ~0.9 m per repetisjon i verden
		vec3 p = obj_pos * sc;
		if (near > 0.001) {
			vec2 ux = p.zy, uy = p.xz, uz = p.xy;
			vec3 ba = texture(detail_albedo, vec3(ux, bark_layer)).rgb * tw.x + texture(detail_albedo, vec3(uy, bark_layer)).rgb * tw.y + texture(detail_albedo, vec3(uz, bark_layer)).rgb * tw.z;
			vec4 bx = texture(detail_nrao, vec3(ux, bark_layer)), by = texture(detail_nrao, vec3(uy, bark_layer)), bz = texture(detail_nrao, vec3(uz, bark_layer));
			vec3 bmean = bark_layer < 0.5 ? vec3(0.1939, 0.135, 0.0823) : vec3(0.2548, 0.1934, 0.0825);
			// Ensfargede lavpoly-stammer (ofte mettet rødbrune): trekk mot ekte barkfarge
			if (!use_tex) c.rgb = mix(c.rgb, bmean * 1.2, 0.7);
			c.rgb *= mix(vec3(1.0), clamp(ba / bmean, vec3(0.3), vec3(2.2)), near * 0.85);
			vec2 nx = bx.xy * 2.0 - 1.0, ny = by.xy * 2.0 - 1.0, nz = bz.xy * 2.0 - 1.0;
			vec3 off_o = vec3(0.0, nx.y, nx.x) * tw.x + vec3(ny.x, 0.0, -ny.y) * tw.y + vec3(nz.x, nz.y, 0.0) * tw.z;
			vec3 off_v = (VIEW_MATRIX * vec4(normalize(mat3(MODEL_MATRIX) * off_o) * length(off_o), 0.0)).xyz;
			NORMAL = normalize(NORMAL + off_v * near * 0.9);
			wn = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
		}
		// Mose: nordsiden (-Z) og nederst på stammen, i flekker
		float north = smoothstep(0.0, 0.7, -wn.z);
		float low = 1.0 - smoothstep(0.3, 2.5, v_h);
		float patchy = texture(detail_albedo, vec3(p.xy * 0.3 + p.z * 0.2, 2.0)).g;
		float moss = clamp(north * 0.7 + low * 0.5, 0.0, 1.0) * smoothstep(0.08, 0.2, patchy) * (1.0 - v_snow);
		vec3 mt = texture(detail_albedo, vec3(p.xz * 1.3 + p.y * 0.7, 2.0)).rgb;
		c.rgb = mix(c.rgb, mt * 1.1, moss * 0.75);
		// Mørkere og fuktigere ved roten
		c.rgb *= mix(0.62, 1.0, smoothstep(0.0, 1.2, v_h));
	}

	// Snø på oversiden om vinteren
	// Snøklumper på oversiden av greiner og bar (ujevnt, ikke hele treet hvitt)
	float clump = ihash(obj_pos * 3.0 + v_hash * 5.0);
	float snow = v_snow * smoothstep(0.05, 0.5, wn.y + (clump - 0.5) * 0.5) * (leafy > 0.5 ? 0.85 : 1.0);
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
