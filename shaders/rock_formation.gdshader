shader_type spatial;

// Klippeformasjoner og store steiner: farges i verdensrom (triplanart) slik at de matcher
// fjellet rundt, uavhengig av modellens egne UV-er og farger.
// Grå stein med varme/kalde flekker, sprekker, mose på flater som vender opp, snø høyt oppe.

uniform float snow_height = 225.0;

global uniform sampler2DArray rock_albedo_arr : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
global uniform sampler2DArray rock_nrao_arr : filter_linear_mipmap_anisotropic, repeat_enable;
global uniform sampler2DArray detail_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
global uniform float snow_line;
global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;

varying vec3 world_pos;
varying vec3 world_normal;

float hash(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u;
	h ^= h >> 16u;
	h *= 2246822519u;
	h ^= h >> 13u;
	h *= 3266489917u;
	h ^= h >> 16u;
	return float(h) * (1.0 / 4294967296.0);
}

float gnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a0 = hash(i) * 6.2831853, a1 = hash(i + vec2(1, 0)) * 6.2831853;
	float a2 = hash(i + vec2(0, 1)) * 6.2831853, a3 = hash(i + vec2(1, 1)) * 6.2831853;
	float va = dot(vec2(cos(a0), sin(a0)), f);
	float vb = dot(vec2(cos(a1), sin(a1)), f - vec2(1, 0));
	float vc = dot(vec2(cos(a2), sin(a2)), f - vec2(0, 1));
	float vd = dot(vec2(cos(a3), sin(a3)), f - vec2(1, 1));
	return mix(mix(va, vb, u.x), mix(vc, vd, u.x), u.y);
}

float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

const mat2 ROT = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));

float gfbm(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 4; i++) {
		s += gnoise(p) * a;
		p = ROT * p * 2.03 + vec2(17.1, 9.7);
		a *= 0.5;
	}
	return s;
}

vec2 grad2(vec2 p, float e) {
	p = mod(p, 2048.0);
	float h0 = gfbm(p);
	return vec2(gfbm(p + vec2(e, 0.0)) - h0, gfbm(p + vec2(0.0, e)) - h0) / e;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 n = normalize(world_normal);
	vec3 w = abs(n);
	w = pow(w, vec3(4.0));
	w /= (w.x + w.y + w.z);

	// Triplanar farge-støy
	float cx = gfbm(world_pos.zy * 0.25), cy = gfbm(world_pos.xz * 0.25), cz = gfbm(world_pos.xy * 0.25);
	float c = cx * w.x + cy * w.y + cz * w.z;
	float big = gfbm(world_pos.xz * 0.02 + world_pos.y * 0.01);
	// Samme grunnfarge, bergart og høydelag som fjellet i terrengshaderen
	vec3 rock = vec3(0.2, 0.19, 0.17);
	float geo1 = vnoise(world_pos.xz * 0.0007 + 3.0), geo2 = vnoise(world_pos.xz * 0.0011 - 11.0);
	vec3 geo = mix(mix(vec3(1.08, 1.1, 1.12), vec3(1.18, 1.0, 0.9), smoothstep(0.4, 0.7, geo1)), vec3(0.72, 0.74, 0.76), smoothstep(0.55, 0.8, geo2));
	float layer = vnoise(vec2(world_pos.y * 0.018 + vnoise(world_pos.xz * 0.003) * 3.0, world_pos.x * 0.0013 + world_pos.z * 0.0009));
	rock *= geo * mix(vec3(0.86, 0.88, 0.9), vec3(1.12, 1.06, 0.96), layer);
	rock *= mix(vec3(0.92, 0.96, 1.04), vec3(1.08, 1.0, 0.9), big * 0.5 + 0.5);
	rock *= 0.85 + c * 0.45;
	// Lagdeling på sidene
	float strata = sin(world_pos.y * 1.6 + gfbm(world_pos.xz * 0.1) * 6.0);
	rock *= 1.0 - smoothstep(0.8, 1.0, strata) * 0.18 * (1.0 - w.y);

	// Mose/gress på flater som vender opp, snø høyt oppe
	float up = smoothstep(0.55, 0.85, n.y);
	float moss_n = gfbm(world_pos.xz * 0.15) * 0.5 + 0.5;
	vec3 moss = vec3(0.24, 0.33, 0.14) * (0.8 + moss_n * 0.4);
	float snow = smoothstep(snow_height - 25.0, snow_height + 15.0, world_pos.y + moss_n * 20.0);
	vec3 col = mix(rock, moss, up * smoothstep(0.35, 0.6, moss_n) * (1.0 - snow));
	col = mix(col, vec3(0.86, 0.89, 0.94), snow * smoothstep(0.3, 0.7, n.y));
	// Ekte bergtekstur (berglaget i terrengteksturene), triplanart, som detalj rundt fargen over
	float dist = length(VERTEX);
	float near = 1.0 - smoothstep(60.0, 400.0, dist);
	float tnear = 1.0 - smoothstep(120.0, 220.0, dist);
	vec3 tex_off = vec3(0.0);
	float tex_rough = 0.85;
	if (tnear > 0.001) {
		float s = 1.0 / 4.0;
		vec2 ux = world_pos.zy * s, uy = world_pos.xz * s, uz = world_pos.xy * s;
		vec4 ax = texture(rock_albedo_arr, vec3(ux, 4.0)), ay = texture(rock_albedo_arr, vec3(uy, 4.0)), az = texture(rock_albedo_arr, vec3(uz, 4.0));
		vec4 mx = texture(rock_nrao_arr, vec3(ux, 4.0)), my = texture(rock_nrao_arr, vec3(uy, 4.0)), mz = texture(rock_nrao_arr, vec3(uz, 4.0));
		vec3 ta = (ax * w.x + ay * w.y + az * w.z).rgb;
		vec3 detail = clamp(ta / vec3(0.0799, 0.0745, 0.0616), vec3(0.3), vec3(2.5));   // snittfarge Rock030
		vec2 nx = mx.xy * 2.0 - 1.0, ny = my.xy * 2.0 - 1.0, nz = mz.xy * 2.0 - 1.0;
		tex_off = (vec3(0.0, nx.y, nx.x) * w.x + vec3(ny.x, 0.0, -ny.y) * w.y + vec3(nz.x, nz.y, 0.0) * w.z) * tnear;
		tex_rough = (mx.z * w.x + my.z * w.y + mz.z * w.z);
		// Mosen får ekte mosetekstur
		vec3 mt = texture(detail_albedo, vec3(world_pos.xz * 0.5, 2.0)).rgb;
		float mossy = up * smoothstep(0.35, 0.6, moss_n) * (1.0 - snow);
		col *= mix(vec3(1.0), mix(detail, clamp(mt / vec3(0.0694, 0.1329, 0.0098), vec3(0.4), vec3(2.0)), mossy), tnear);
	}
	// Årstidssnø på toppen av steinene
	float temp = texture(world_map, (world_pos.xz - world_rect.xy) * world_rect.zw).r;
	float ssnow = smoothstep(temp - 0.03, temp + 0.03, max(snow_line, 0.13)) * smoothstep(0.3, 0.6, n.y + (moss_n - 0.5) * 0.4);
	col = mix(col, vec3(0.82, 0.85, 0.91), ssnow);
	ALBEDO = col;

	// Bump (triplanar)
	vec2 gx = grad2(world_pos.zy * 0.35, 0.1);
	vec2 gy = grad2(world_pos.xz * 0.35, 0.1);
	vec2 gz = grad2(world_pos.xy * 0.35, 0.1);
	vec3 pert = vec3(0.0, gx.y, gx.x) * w.x + vec3(gy.x, 0.0, gy.y) * w.y + vec3(gz.x, gz.y, 0.0) * w.z;
	vec3 bn = normalize(n - pert * 0.9 * near * (1.0 - tnear * 0.8) + tex_off * 0.9);
	NORMAL = normalize((VIEW_MATRIX * vec4(bn, 0.0)).xyz);
	ROUGHNESS = mix(mix(0.85, tex_rough, tnear), 0.7, max(snow, ssnow));
	SPECULAR = 0.25;
}
