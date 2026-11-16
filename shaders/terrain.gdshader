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

// Teksturlag (ambientCG, se scripts/fetch_terrain_textures.py). Skive i = lag i:
// 0 eng, 1 lauvskogbunn, 2 barskogbunn/sti, 3 lynghei/myr/jord, 4 berg, 5 ur/grus, 6 sand, 7 snø.
// albedo_arr: rgb = farge, a = høyde.  nrao_arr: rg = normal (OpenGL), b = ruhet, a = AO.
uniform sampler2DArray albedo_arr : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray nrao_arr : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 layer_mean[8];          // snittfarge per lag (lineær) - teksturen brukes som detalj rundt sonefargen
uniform float tex_near = 140.0;      // teksturene tones ut mellom disse avstandene
uniform float tex_far = 220.0;
const float TILE[8] = { 2.5, 3.0, 3.0, 3.5, 6.0, 2.5, 4.0, 4.0 };   // meter per repetisjon

varying vec4 lw0;   // lagvekter 0-3 (CUSTOM0)
varying vec4 lw1;   // lagvekter 4-7 (CUSTOM1)
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

// Triplanar: sampler støyen i de tre aksplanene og blander etter normalen,
// så mønsteret ikke strekkes loddrett på stup (det ga "dratt opp av bakken"-inntrykket)
vec3 tri_w(vec3 n) {
	vec3 w = pow(abs(n), vec3(4.0));
	return w / (w.x + w.y + w.z);
}
float tri_vnoise(vec3 p, vec3 w, float s) {
	return vnoise(p.zy * s) * w.x + vnoise(p.xz * s) * w.y + vnoise(p.xy * s + 17.0) * w.z;
}
float tri_fbm(vec3 p, vec3 w, float s) {
	return fbm(p.zy * s) * w.x + fbm(p.xz * s) * w.y + fbm(p.xy * s + 17.0) * w.z;
}

// Sprekker i berget: kantene i et Voronoi-mønster (0 = midt i en flate, 1 = i sprekken)
float cracks2(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	float d1 = 8.0, d2 = 8.0;
	for (int y = -1; y <= 1; y++)
	for (int x = -1; x <= 1; x++) {
		vec2 g = vec2(float(x), float(y));
		vec2 o = vec2(hash(i + g), hash(i + g + 31.7));
		float d = length(g + o - f);
		if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) { d2 = d; }
	}
	return 1.0 - smoothstep(0.0, 0.08, d2 - d1);
}
float tri_cracks(vec3 p, vec3 w, float s) {
	return cracks2(p.zy * s) * w.x + cracks2(p.xz * s) * w.y + cracks2(p.xy * s + 17.0) * w.z;
}

// Ett teksturlag: plant (xz) eller triplanart. Gir farge+høyde, normalforskyvning i verdensrom og ruhet/AO.
void layer_sample(int i, vec3 p, vec3 tw, bool tri, vec2 warp, out vec4 a, out vec3 off, out vec2 ra) {
	float s = 1.0 / TILE[i];
	float li = float(i);
	if (!tri) {
		vec2 uv = p.xz * s + warp;
		a = texture(albedo_arr, vec3(uv, li));
		vec4 m = texture(nrao_arr, vec3(uv, li));
		vec2 nt = m.xy * 2.0 - 1.0;
		off = vec3(nt.x, 0.0, -nt.y);
		ra = m.zw;
	} else {
		vec2 ux = p.zy * s + warp, uy = p.xz * s + warp, uz = p.xy * s + warp;
		vec4 ax = texture(albedo_arr, vec3(ux, li)), ay = texture(albedo_arr, vec3(uy, li)), az = texture(albedo_arr, vec3(uz, li));
		vec4 mx = texture(nrao_arr, vec3(ux, li)), my = texture(nrao_arr, vec3(uy, li)), mz = texture(nrao_arr, vec3(uz, li));
		a = ax * tw.x + ay * tw.y + az * tw.z;
		vec2 nx = mx.xy * 2.0 - 1.0, ny = my.xy * 2.0 - 1.0, nz = mz.xy * 2.0 - 1.0;
		off = vec3(0.0, nx.y, nx.x) * tw.x + vec3(ny.x, 0.0, -ny.y) * tw.y + vec3(nz.x, nz.y, 0.0) * tw.z;
		ra = mx.zw * tw.x + my.zw * tw.y + mz.zw * tw.z;
	}
}

void vertex() {
	lw0 = CUSTOM0;
	lw1 = CUSTOM1;
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 base = pow(COLOR.rgb, vec3(2.2));
	float ao = COLOR.a * 1.25;
	float dist = length(VERTEX);
	float near = 1.0 - smoothstep(120.0, 700.0, dist);

	vec3 n = normalize(world_normal);
	vec3 tw = tri_w(n);

	// --- Fargevariasjon: store flekker (tørt/frodig) og små nyanser
	float big = vnoise(world_pos.xz * 0.012);
	float mid = vnoise(world_pos.xz * 0.07 + 31.0);
	float fine = vnoise(world_pos.xz * 0.6) * 0.5 + vnoise(world_pos.xz * 2.3) * 0.5;
	// Ujevn grense mellom gress og berg (ikke en glatt linje langs en høydekurve)
	float steep = smoothstep(0.82, 0.6, n.y + (mid - 0.5) * 0.14 + (fine - 0.5) * 0.08);
	vec3 col = base;
	vec3 dry = base * vec3(1.18, 1.08, 0.72);
	vec3 lush = base * vec3(0.82, 0.95, 0.80);
	col = mix(col, dry, smoothstep(0.55, 0.85, big) * 0.55 * (1.0 - steep));
	col = mix(col, lush, smoothstep(0.45, 0.15, mid) * 0.45 * (1.0 - steep));
	float tf = 1.0 - smoothstep(tex_near, tex_far, dist);   // hvor mye ekte tekstur (nært)
	col *= mix(1.0, 0.82 + fine * 0.36, near * (1.0 - tf * 0.7));

	// --- Fjell: fargevariasjon (varme/kalde flekker, lav) og svake, uregelmessige steinlag
	float rv1 = tri_vnoise(world_pos, tw, 0.008);
	float rv2 = steep > 0.01 ? tri_fbm(world_pos, tw, 0.045) : fbm(world_pos.xz * 0.045);
	vec3 warm = vec3(1.10, 1.00, 0.88), cool = vec3(0.88, 0.94, 1.04);
	vec3 rock_tint = mix(cool, warm, rv1) * (0.80 + rv2 * 0.40);
	float strata_n = fbm(world_pos.xz * 0.012) * 14.0 + fbm(world_pos.xz * 0.08) * 3.0;
	float strata = sin(world_pos.y * 0.33 + strata_n);
	float band = smoothstep(0.75, 1.0, strata) * fbm(world_pos.xz * 0.02 + 5.0);
	vec3 rock_col = col * rock_tint * (1.0 - band * 0.25);
	// Store toneflekker over hele fjellsider (lyse og mørke partier, ikke én jevn grå vegg)
	float macro = tri_vnoise(world_pos, tw, 0.0025) * 0.6 + tri_vnoise(world_pos, tw, 0.006) * 0.4;
	rock_col *= mix(0.78, 1.18, macro);
	// Mørke vannsig nedover stupene (uregelmessige, ikke et kamstriper-mønster)
	float seep = vnoise(vec2(dot(world_pos.xz, vec2(0.7, 0.7)) * 0.09, world_pos.y * 0.006));
	rock_col *= 1.0 - smoothstep(0.7, 0.9, seep) * 0.25 * smoothstep(0.75, 0.95, steep);
	// Lav/mose i forsenkninger på fjellet
	rock_col = mix(rock_col, rock_col * vec3(0.85, 0.95, 0.70), smoothstep(0.55, 0.8, rv2) * 0.5);
	// Sprekker og kanter i berget (bare nært)
	if (near > 0.01 && steep > 0.01) {
		float cr = tri_cracks(world_pos, tw, 0.11) * 0.7 + tri_cracks(world_pos, tw, 0.37) * 0.3;
		rock_col *= 1.0 - cr * 0.45 * near * (1.0 - tf * 0.8);
		// Lyse kanter rett ved sprekkene (slitt stein)
		rock_col *= 1.0 + tri_vnoise(world_pos, tw, 1.3) * 0.12 * near;
	}
	float sat = max(base.r, max(base.g, base.b)) - min(base.r, min(base.g, base.b));
	float grey = smoothstep(0.06, 0.02, sat) * smoothstep(0.75, 0.5, base.g);   // stein (ikke snø)
	col = mix(col, rock_col, max(steep, grey));

	// --- Ekte teksturer nært: høydeblanding mellom lagene, brukt som detalj rundt sonefargen
	vec3 tex_off = vec3(0.0);
	float tex_rough = 0.85, tex_ao = 1.0;
	vec2 warp = vec2(vnoise(world_pos.xz * 0.05), vnoise(world_pos.xz * 0.05 + 71.0)) * 0.35;
	if (tf > 0.001) {
		float wts[8] = { lw0.x, lw0.y, lw0.z, lw0.w, lw1.x, lw1.y, lw1.z, lw1.w };
		bool tri = n.y < 0.75;
		vec4 sa[8];
		vec3 so[8];
		vec2 sr[8];
		float sc[8];
		float best = -1.0;
		for (int i = 0; i < 8; i++) {
			sc[i] = -1.0;
			if (wts[i] > 0.03) {
				layer_sample(i, world_pos, tw, tri, warp, sa[i], so[i], sr[i]);
				sc[i] = wts[i] + sa[i].a * 0.6;
				best = max(best, sc[i]);
			}
		}
		vec3 tc = vec3(0.0), tm = vec3(0.0), to = vec3(0.0);
		vec2 tr = vec2(0.0);
		float tot = 0.0;
		for (int i = 0; i < 8; i++) {
			if (sc[i] < 0.0) continue;
			float f = max(sc[i] - best + 0.25, 0.0);
			tc += sa[i].rgb * f; tm += layer_mean[i] * f; to += so[i] * f; tr += sr[i] * f; tot += f;
		}
		if (tot > 0.0) {
			tc /= tot; tm /= tot; to /= tot; tr /= tot;
			vec3 detail = clamp(tc / max(tm, vec3(0.004)), vec3(0.25), vec3(3.0));
			col *= mix(vec3(1.0), detail, tf);
			tex_off = to * tf;
			tex_rough = tr.x;
			tex_ao = mix(1.0, tr.y, tf);
		}
	}

	// --- Årstidssnø (vinter / snøvær)
	float temp = texture(world_map, (world_pos.xz - world_rect.xy) * world_rect.zw).r;
	float edge = (vnoise(world_pos.xz * 0.05) - 0.5) * 0.06 + (fine - 0.5) * 0.02;
	float snow = smoothstep(temp - 0.03, temp + 0.03, snow_line + edge);
	snow *= smoothstep(0.6, 0.8, n.y) * smoothstep(0.2, 0.8, world_pos.y);
	// Snøfonner i renner og søkk høyt til fjells (ligger igjen hele sommeren)
	float concave = smoothstep(0.92, 0.7, ao);
	float alt = smoothstep(135.0, 185.0, world_pos.y + (vnoise(world_pos.xz * 0.02) - 0.5) * 40.0);
	float gully = alt * concave * smoothstep(0.35, 0.55, n.y) * smoothstep(0.55, 0.35, temp + snow_line * 0.3 - 0.1);
	snow = max(snow, smoothstep(0.25, 0.55, gully + (fine - 0.5) * 0.2));
	vec3 snow_col = vec3(0.80, 0.83, 0.90) * (0.92 + fine * 0.12);
	if (snow > 0.01 && tf > 0.001) {
		vec4 sa7; vec3 so7; vec2 sr7;
		layer_sample(7, world_pos, tw, false, warp, sa7, so7, sr7);
		snow_col *= mix(vec3(1.0), clamp(sa7.rgb / max(layer_mean[7], vec3(0.004)), vec3(0.5), vec3(1.5)), tf);
		tex_off = mix(tex_off, so7 * tf, snow);
	}
	col = mix(col, snow_col, snow);

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
		bn = normalize(n - pert * near * (1.0 - snow * 0.7) * (1.0 - tf * 0.85));
	}
	bn = normalize(bn + tex_off * 0.9);
	NORMAL = normalize((VIEW_MATRIX * vec4(bn, 0.0)).xyz);

	ALBEDO = col;
	AO = clamp(ao, 0.0, 1.0) * tex_ao;
	AO_LIGHT_AFFECT = 0.25;
	ROUGHNESS = mix(mix(mix(0.95, 0.8, steep), tex_rough, tf), 0.3, wet * 0.85);
	if (snow > 0.0) ROUGHNESS = mix(ROUGHNESS, 0.7, snow);
	SPECULAR = 0.2 + wet * 0.3;
}
