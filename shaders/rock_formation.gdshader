shader_type spatial;

// Klippeformasjoner og store steiner: farges i verdensrom (triplanart) slik at de matcher
// fjellet rundt, uavhengig av modellens egne UV-er og farger.
// Grå stein med varme/kalde flekker, sprekker, mose på flater som vender opp, snø høyt oppe.

uniform float snow_height = 225.0;

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
	vec3 rock = vec3(0.30, 0.29, 0.27);
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
	ALBEDO = col;

	// Bump (triplanar)
	float dist = length(VERTEX);
	float near = 1.0 - smoothstep(60.0, 400.0, dist);
	vec2 gx = grad2(world_pos.zy * 0.35, 0.1);
	vec2 gy = grad2(world_pos.xz * 0.35, 0.1);
	vec2 gz = grad2(world_pos.xy * 0.35, 0.1);
	vec3 pert = vec3(0.0, gx.y, gx.x) * w.x + vec3(gy.x, 0.0, gy.y) * w.y + vec3(gz.x, gz.y, 0.0) * w.z;
	vec3 bn = normalize(n - pert * 0.9 * near);
	NORMAL = normalize((VIEW_MATRIX * vec4(bn, 0.0)).xyz);
	ROUGHNESS = mix(0.85, 0.7, snow);
	SPECULAR = 0.25;
}
