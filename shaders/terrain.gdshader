shader_type spatial;
render_mode cull_back;   // dobbeltsidig gir skyggeakne; skjørtene har trekanter begge veier

// Terreng: bakkefarge fra vertex color (klimasone/helning, beregnet på CPU),
// COLOR.a = okklusjon/krumning (0..1 => 0..1.25).
// Shaderen legger på: prosedyrisk ujevnhet i lyset (bump), steinlag i bratte partier,
// fargeflekker i gresset og detaljstøy som tones ut på avstand.

uniform float bump_ground = 0.3;
uniform float bump_rock = 0.9;

global uniform float snow_line;      // årstidssnø: temperatur snøen går ned til (Weather.cs)
global uniform float wetness;        // 0..1, regn
global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;

varying vec3 world_pos;
varying vec3 world_normal;

// Heltallsbasert hash for gitterpunkter. (En fract(p*stor)-hash mister presisjon ved store
// koordinater, og GPU-en regner da samme hjørne ulikt fra hver side av en cellegrense,
// som gir rette striper i lyset.)
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

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic: glatt gradient (ingen ruter)
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x),
	           mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

const mat2 ROT = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));   // roter hver oktav (bryter rutenettet)

float fbm(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 4; i++) {
		s += vnoise(p) * a;
		p = ROT * p * 2.03 + vec2(17.1, 9.7);
		a *= 0.5;
	}
	return s;
}

// Gradientstøy (Perlin-type). Verdistøy gir null helning langs cellegrensene, som synes som
// rette striper i lyset; gradientstøy har ikke det problemet og brukes derfor til bump.
float gnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a0 = hash(i) * 6.2831853;
	float a1 = hash(i + vec2(1, 0)) * 6.2831853;
	float a2 = hash(i + vec2(0, 1)) * 6.2831853;
	float a3 = hash(i + vec2(1, 1)) * 6.2831853;
	float va = dot(vec2(cos(a0), sin(a0)), f);
	float vb = dot(vec2(cos(a1), sin(a1)), f - vec2(1, 0));
	float vc = dot(vec2(cos(a2), sin(a2)), f - vec2(0, 1));
	float vd = dot(vec2(cos(a3), sin(a3)), f - vec2(1, 1));
	return mix(mix(va, vb, u.x), mix(vc, vd, u.x), u.y);
}

float gfbm(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 4; i++) {
		s += gnoise(p) * a;
		p = ROT * p * 2.03 + vec2(17.1, 9.7);
		a *= 0.5;
	}
	return s;
}

// Gradient av gradientstøy-fbm i et plan (endelig differanse)
vec2 fbm_grad(vec2 p, float e) {
	p = mod(p, 2048.0);   // hold koordinatene små (presisjon i hash)
	float h0 = gfbm(p);
	return vec2(gfbm(p + vec2(e, 0.0)) - h0, gfbm(p + vec2(0.0, e)) - h0) / e;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 base = pow(COLOR.rgb, vec3(2.2));
	float ao = COLOR.a * 1.25;
	float dist = length(VERTEX);
	float near = 1.0 - smoothstep(120.0, 700.0, dist);

	vec3 n = normalize(world_normal);
	float steep = smoothstep(0.82, 0.6, n.y);           // 0 = flatt, 1 = bratt fjell

	// --- Fargevariasjon: store flekker (tørt/frodig) og små nyanser
	float big = vnoise(world_pos.xz * 0.012);
	float mid = vnoise(world_pos.xz * 0.07 + 31.0);
	float fine = vnoise(world_pos.xz * 0.6) * 0.5 + vnoise(world_pos.xz * 2.3) * 0.5;
	vec3 col = base;
	vec3 dry = base * vec3(1.18, 1.08, 0.72);
	vec3 lush = base * vec3(0.82, 0.95, 0.80);
	col = mix(col, dry, smoothstep(0.55, 0.85, big) * 0.55 * (1.0 - steep));
	col = mix(col, lush, smoothstep(0.45, 0.15, mid) * 0.45 * (1.0 - steep));
	col *= mix(1.0, 0.82 + fine * 0.36, near);

	// --- Fjell: fargevariasjon (varme/kalde flekker, lav) og svake, uregelmessige steinlag
	float rv1 = vnoise(world_pos.xz * 0.008 + world_pos.y * 0.004);
	float rv2 = fbm(world_pos.xz * 0.045 + vec2(world_pos.y * 0.03, 0.0));
	vec3 warm = vec3(1.10, 1.00, 0.88), cool = vec3(0.88, 0.94, 1.04);
	vec3 rock_tint = mix(cool, warm, rv1) * (0.80 + rv2 * 0.40);
	float strata_n = fbm(world_pos.xz * 0.012) * 14.0 + fbm(world_pos.xz * 0.08) * 3.0;
	float strata = sin(world_pos.y * 0.33 + strata_n);
	float band = smoothstep(0.75, 1.0, strata) * fbm(world_pos.xz * 0.02 + 5.0);
	vec3 rock_col = col * rock_tint * (1.0 - band * 0.25);
	// Lav/mose i forsenkninger på fjellet
	rock_col = mix(rock_col, rock_col * vec3(0.85, 0.95, 0.70), smoothstep(0.55, 0.8, rv2) * 0.5);
	float sat = max(base.r, max(base.g, base.b)) - min(base.r, min(base.g, base.b));
	float grey = smoothstep(0.06, 0.02, sat) * smoothstep(0.75, 0.5, base.g);   // stein (ikke snø)
	col = mix(col, rock_col, max(steep, grey));

	// --- Årstidssnø (vinter / snøvær)
	float temp = texture(world_map, (world_pos.xz - world_rect.xy) * world_rect.zw).r;
	float edge = (vnoise(world_pos.xz * 0.05) - 0.5) * 0.06 + (fine - 0.5) * 0.02;
	float snow = smoothstep(temp - 0.03, temp + 0.03, snow_line + edge);
	snow *= smoothstep(0.6, 0.8, n.y) * smoothstep(0.2, 0.8, world_pos.y);
	col = mix(col, vec3(0.80, 0.83, 0.90) * (0.92 + fine * 0.12), snow);

	// --- Våt bakke (mørkere og blankere)
	float wet = wetness * (1.0 - snow);
	col *= 1.0 - wet * 0.32;

	// --- Okklusjon/krumning
	col *= mix(0.62, 1.0, clamp(ao, 0.0, 1.0)) * (1.0 + max(ao - 1.0, 0.0) * 0.6);

	// --- Bump: gress/jord i XZ-planet, triplanart på bratte flater
	vec3 bn = n;
	if (near > 0.001) {
		float near_ground = 1.0 - smoothstep(25.0, 90.0, dist);   // fin bump bare nært (unngår flimmer)
		vec2 g_ground = fbm_grad(world_pos.xz * 0.7, 0.06) * bump_ground * near_ground
		              + fbm_grad(world_pos.xz * 0.08, 0.3) * 0.6;    // store, rolige ujevnheter
		vec3 pert = vec3(g_ground.x, 0.0, g_ground.y) * (1.0 - steep);
		if (steep > 0.01) {
			vec2 gx = fbm_grad(world_pos.zy * vec2(0.16, 0.42), 0.12) * bump_rock;
			vec2 gz = fbm_grad(world_pos.xy * vec2(0.16, 0.42), 0.12) * bump_rock;
			vec3 w = abs(n);
			w /= (w.x + w.z + 0.0001);
			pert += (vec3(0.0, gx.y, gx.x) * w.x + vec3(gz.x, gz.y, 0.0) * w.z) * steep;
		}
		bn = normalize(n - pert * near * (1.0 - snow * 0.7));
	}
	NORMAL = normalize((VIEW_MATRIX * vec4(bn, 0.0)).xyz);

	ALBEDO = col;
	AO = clamp(ao, 0.0, 1.0);
	AO_LIGHT_AFFECT = 0.25;
	ROUGHNESS = mix(mix(0.95, 0.8, steep), 0.3, wet * 0.85);
	if (snow > 0.0) ROUGHNESS = mix(ROUGHNESS, 0.7, snow);
	SPECULAR = 0.2 + wet * 0.3;
}
