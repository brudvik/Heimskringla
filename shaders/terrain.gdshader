shader_type spatial;
render_mode cull_back;   // double-sided causes shadow acne; the skirts have triangles facing both ways
#include "res://shaders/cloudshadow.gdshaderinc"

// Terrain: ground color from vertex color (climate zone/slope, computed on the CPU),
// COLOR.a = occlusion/curvature (0..1 => 0..1.25).
// The shader adds: procedural unevenness in the lighting (bump), rock strata on steep parts,
// color patches in the grass and detail noise that fades out with distance.

uniform float bump_ground = 0.3;
uniform float bump_rock = 0.9;

global uniform float snow_line;      // seasonal snow: temperature the snow reaches down to (Weather.cs)
global uniform float wetness;        // 0..1, rain
global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;
global uniform sampler2D world_masks : filter_linear;   // r = gullies, g = curvature (0.5 flat, > hollow), b = bedrock

// Texture layers (ambientCG, see scripts/fetch_terrain_textures.py). Slice i = layer i:
// 0 meadow, 1 deciduous forest floor, 2 conifer forest floor/path, 3 heath/bog/soil, 4 rock, 5 scree/gravel, 6 sand, 7 snow.
// albedo_arr: rgb = color, a = height.  nrao_arr: rg = normal (OpenGL), b = roughness, a = AO.
uniform sampler2DArray albedo_arr : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray nrao_arr : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 layer_mean[8];          // mean color per layer (linear) - the texture is used as detail around the zone color
uniform float tex_near = 140.0;      // textures fade out between these distances
uniform float tex_far = 220.0;
const float TILE[8] = { 2.5, 3.0, 3.0, 3.5, 6.0, 2.5, 4.0, 4.0 };   // meters per repeat

varying vec4 lw0;   // layer weights 0-3 (CUSTOM0)
varying vec4 lw1;   // layer weights 4-7 (CUSTOM1)
varying vec3 world_pos;
varying vec3 world_normal;

// Integer-based hash for lattice points. (A fract(p*large) hash loses precision at large
// coordinates, and the GPU then computes the same corner differently from each side of a cell boundary,
// which gives straight stripes in the lighting.)
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
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic: smooth gradient (no grid squares)
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x),
	           mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

const mat2 ROT = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));   // rotate each octave (breaks up the grid)

float fbm(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 4; i++) {
		s += vnoise(p) * a;
		p = ROT * p * 2.03 + vec2(17.1, 9.7);
		a *= 0.5;
	}
	return s;
}

// Gradient noise (Perlin-style). Value noise has zero slope along cell boundaries, which shows up as
// straight stripes in the lighting; gradient noise doesn't have that problem and is therefore used for bump.
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

// Gradient of gradient-noise fbm in a plane (finite difference)
vec2 fbm_grad(vec2 p, float e) {
	p = mod(p, 2048.0);   // keep coordinates small (hash precision)
	float h0 = gfbm(p);
	return vec2(gfbm(p + vec2(e, 0.0)) - h0, gfbm(p + vec2(0.0, e)) - h0) / e;
}

// Triplanar: samples the noise in the three axis planes and blends by the normal,
// so the pattern isn't stretched vertically on cliffs (that gave the "pulled up out of the ground" look)
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

// Cracks in the rock: the edges of a Voronoi pattern (0 = middle of a face, 1 = in the crack)
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

// One texture layer: planar (xz) or triplanar. Returns color+height, normal offset in world space and roughness/AO.
void layer_sample(int i, vec3 p, vec3 tw, bool tri, vec2 warp, float smul, out vec4 a, out vec3 off, out vec2 ra) {
	float s = smul / TILE[i];
	float li = float(i);
	if (!tri) {
		// Anti-tiling: two samples with different rotation/scale/offset, blended with low-frequency
		// noise (and the height map, so the transition follows the structure of the material)
		vec2 uv = p.xz * s + warp;
		float ang = 1.9 + li * 0.7;
		mat2 R = mat2(vec2(cos(ang), sin(ang)), vec2(-sin(ang), cos(ang)));
		vec2 uv2 = R * (p.xz * s * 0.83) + vec2(0.37, 0.71) * (li + 1.0);
		float bn = vnoise(p.xz * 0.11 + li * 13.0) * 0.7 + vnoise(p.xz * 0.37) * 0.3;
		// Only one sample outside the transition zone (saves half the lookups on most of the ground)
		vec4 a1 = vec4(0.0), a2 = vec4(0.0), m1 = vec4(0.5, 0.5, 0.8, 1.0), m2 = m1;
		if (bn < 0.72) { a1 = texture(albedo_arr, vec3(uv, li)); m1 = texture(nrao_arr, vec3(uv, li)); }
		if (bn > 0.28) { a2 = texture(albedo_arr, vec3(uv2, li)); m2 = texture(nrao_arr, vec3(uv2, li)); }
		float b = bn < 0.28 ? 0.0 : bn > 0.72 ? 1.0 : smoothstep(0.4, 0.6, bn + (a2.a - a1.a) * 0.25);
		a = mix(a1, a2, b);
		vec2 n1 = m1.xy * 2.0 - 1.0;
		vec2 n2 = transpose(R) * (m2.xy * 2.0 - 1.0);   // the normal is rotated back
		vec2 nt = mix(n1, n2, b);
		off = vec3(nt.x, 0.0, -nt.y);
		ra = mix(m1.zw, m2.zw, b);
	} else {
		// Anti-tiling on cliffs: a coarser, offset sample is blended in as patches (two samples only in the transition)
		float bn = vnoise(p.xz * 0.06 + p.y * 0.045 + li * 7.0) * 0.7 + vnoise(vec2(p.x + p.z, p.y) * 0.21) * 0.3;
		float b = smoothstep(0.38, 0.62, bn);
		float s2 = b > 0.0 ? 0.53 : 1.0;
		vec2 o2 = b > 0.0 ? vec2(0.31, 0.77) : vec2(0.0);
		vec2 ux = p.zy * s + warp, uy = p.xz * s + warp, uz = p.xy * s + warp;
		vec4 ax = texture(albedo_arr, vec3(ux, li)), ay = texture(albedo_arr, vec3(uy, li)), az = texture(albedo_arr, vec3(uz, li));
		vec4 mx = texture(nrao_arr, vec3(ux, li)), my = texture(nrao_arr, vec3(uy, li)), mz = texture(nrao_arr, vec3(uz, li));
		if (b > 0.0) {
			vec2 vx = p.zy * s * s2 + o2, vy = p.xz * s * s2 + o2, vz = p.xy * s * s2 + o2;
			ax = mix(ax, texture(albedo_arr, vec3(vx, li)), b); ay = mix(ay, texture(albedo_arr, vec3(vy, li)), b); az = mix(az, texture(albedo_arr, vec3(vz, li)), b);
			mx = mix(mx, texture(nrao_arr, vec3(vx, li)), b); my = mix(my, texture(nrao_arr, vec3(vy, li)), b); mz = mix(mz, texture(nrao_arr, vec3(vz, li)), b);
		}
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

	// --- Color variation: large patches (dry/lush) and small nuances
	float big = vnoise(world_pos.xz * 0.012);
	float mid = vnoise(world_pos.xz * 0.07 + 31.0);
	float fine = vnoise(world_pos.xz * 0.6) * 0.5 + vnoise(world_pos.xz * 2.3) * 0.5;
	// Ragged boundary between grass and rock (not a smooth line along a contour)
	float steep = smoothstep(0.82, 0.6, n.y + (mid - 0.5) * 0.14 + (fine - 0.5) * 0.08);
	vec3 col = base;
	vec3 dry = base * vec3(1.18, 1.08, 0.72);
	vec3 lush = base * vec3(0.82, 0.95, 0.80);
	col = mix(col, dry, smoothstep(0.55, 0.85, big) * 0.55 * (1.0 - steep));
	col = mix(col, lush, smoothstep(0.45, 0.15, mid) * 0.45 * (1.0 - steep));
	float tf = 1.0 - smoothstep(tex_near, tex_far, dist);   // how much real texture (near)
	col *= mix(1.0, 0.82 + fine * 0.36, near * (1.0 - tf * 0.7));

	// --- Mountains: color variation (warm/cool patches, lichen) and faint, irregular rock strata
	float rv1 = tri_vnoise(world_pos, tw, 0.008);
	float rv2 = steep > 0.01 ? tri_fbm(world_pos, tw, 0.045) : fbm(world_pos.xz * 0.045);
	vec3 warm = vec3(1.10, 1.00, 0.88), cool = vec3(0.88, 0.94, 1.04);
	vec3 rock_tint = mix(cool, warm, rv1) * (0.80 + rv2 * 0.40);
	// Rock type per region: pink/warm granite, dark gabbro, light grey gneiss (large, soft transitions)
	float geo1 = vnoise(world_pos.xz * 0.0007 + 3.0), geo2 = vnoise(world_pos.xz * 0.0011 - 11.0);
	vec3 granite = vec3(1.18, 1.0, 0.9), gabbro = vec3(0.72, 0.74, 0.76), gneiss = vec3(1.08, 1.1, 1.12);
	vec3 geo = mix(mix(gneiss, granite, smoothstep(0.4, 0.7, geo1)), gabbro, smoothstep(0.55, 0.8, geo2));
	rock_tint *= geo;
	// Irregular rock bands: thickness and spacing vary up and around the mountain (no fixed period)
	float strata_n = fbm(world_pos.xz * 0.012) * 14.0 + fbm(world_pos.xz * 0.08) * 3.0;
	float sy = world_pos.y * 0.11 + strata_n * 0.35;
	float band = smoothstep(0.62, 0.85, vnoise(vec2(sy, dot(world_pos.xz, vec2(0.004, 0.003)))))
	           * smoothstep(0.35, 0.7, vnoise(world_pos.xz * 0.01 + world_pos.y * 0.008));
	// Large color bands in the mountain that change with height (each mountainside its own sequence)
	float layer = vnoise(vec2(world_pos.y * 0.018 + vnoise(world_pos.xz * 0.003) * 3.0, world_pos.x * 0.0013 + world_pos.z * 0.0009));
	rock_tint *= mix(vec3(0.86, 0.88, 0.9), vec3(1.12, 1.06, 0.96), layer);
	vec3 rock_col = col * rock_tint * (1.0 - band * 0.25);
	// Large tonal patches across whole mountainsides (light and dark areas, not one uniform grey wall)
	float macro = tri_vnoise(world_pos, tw, 0.0025) * 0.6 + tri_vnoise(world_pos, tw, 0.006) * 0.4;
	rock_col *= mix(0.78, 1.18, macro);
	// Dark water seeps down the cliffs (irregular, not a comb-stripe pattern)
	// Masks from world generation: gullies (where water collects), hollows and ridges
	vec4 wm = texture(world_masks, (world_pos.xz - world_rect.xy) * world_rect.zw);
	float m_gully = smoothstep(0.45, 0.85, wm.r + (fine - 0.5) * 0.1);
	float m_hollow = smoothstep(0.52, 0.7, wm.g), m_ridge = smoothstep(0.48, 0.3, wm.g);
	// Wet, dark streaks where water runs down the cliffs
	float streak = m_gully * smoothstep(0.4, 0.75, vnoise(vec2(dot(world_pos.xz, vec2(0.7, -0.7)) * 0.35, world_pos.y * 0.02)));
	rock_col *= 1.0 - streak * 0.35 * smoothstep(0.5, 0.9, steep);
	// Ridges and edges: lighter, exposed rock; hollows: darker and greener (moss, moisture)
	rock_col *= 1.0 + m_ridge * 0.15;
	rock_col = mix(rock_col, rock_col * vec3(0.75, 0.88, 0.62), m_hollow * 0.45);
	// Yellow/orange map lichen in patches on rock that faces the sun (south, +Z) and isn't too steep
	float lichen = smoothstep(0.62, 0.78, tri_vnoise(world_pos, tw, 0.35)) * smoothstep(0.2, 0.8, n.z * 0.5 + 0.5) * smoothstep(0.2, 0.6, n.y + 0.3) * near;
	rock_col = mix(rock_col, rock_col * vec3(1.35, 1.1, 0.55), lichen * 0.45);
	// Lichen/moss in depressions on the mountain
	rock_col = mix(rock_col, rock_col * vec3(0.85, 0.95, 0.70), smoothstep(0.55, 0.8, rv2) * 0.5);
	// Cracks and edges in the rock (near only)
	if (near > 0.01 && steep > 0.01) {
		float cr = tri_cracks(world_pos, tw, 0.11) * 0.7 + tri_cracks(world_pos, tw, 0.37) * 0.3;
		rock_col *= 1.0 - cr * 0.45 * near * (1.0 - tf * 0.8);
		// Light edges right next to the cracks (worn stone)
		rock_col *= 1.0 + tri_vnoise(world_pos, tw, 1.3) * 0.12 * near;
	}
	float sat = max(base.r, max(base.g, base.b)) - min(base.r, min(base.g, base.b));
	float grey = smoothstep(0.06, 0.02, sat) * smoothstep(0.75, 0.5, base.g);   // stone (not snow)
	col = mix(col, rock_col, max(steep, grey));
	col = mix(col, col * vec3(0.85, 0.95, 0.8), m_hollow * (1.0 - steep) * 0.35);
	col = mix(col, col * vec3(1.1, 1.05, 0.85), m_ridge * (1.0 - steep) * 0.3);

	// --- Real textures up close: height blending between layers, used as detail around the zone color
	vec3 tex_off = vec3(0.0);
	float tex_rough = 0.85, tex_ao = 1.0;
	vec2 warp = vec2(vnoise(world_pos.xz * 0.05), vnoise(world_pos.xz * 0.05 + 71.0)) * 0.35;
	if (tf > 0.001) {
		float wts[8] = { lw0.x, lw0.y, lw0.z, lw0.w, lw1.x, lw1.y, lw1.z, lw1.w };
		// Scree and gravel collect in the gullies below the cliffs
		wts[5] += m_gully * wm.b * smoothstep(0.55, 0.8, n.y) * 0.8;
		// Coarser texture scale at mid distance (same material, no fine "grainy" repetition)
		float coarse = smoothstep(35.0, 75.0, dist);
		bool tri = n.y < 0.75;
		vec4 sa[8];
		vec3 so[8];
		vec2 sr[8];
		float sc[8];
		float best = -1.0;
		for (int i = 0; i < 8; i++) {
			sc[i] = -1.0;
			if (wts[i] > 0.03) {
				if (coarse < 0.999) layer_sample(i, world_pos, tw, tri, warp, 1.0, sa[i], so[i], sr[i]);
				if (coarse > 0.001) {
					vec4 ca; vec3 co; vec2 cr;
					layer_sample(i, world_pos, tw, tri, warp + 0.5, 0.3, ca, co, cr);
					if (coarse >= 0.999) { sa[i] = ca; so[i] = co; sr[i] = cr; }
					else { sa[i] = mix(sa[i], ca, coarse); so[i] = mix(so[i], co, coarse); sr[i] = mix(sr[i], cr, coarse); }
				}
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

	// --- Seasonal snow (winter / snowfall)
	float temp = texture(world_map, (world_pos.xz - world_rect.xy) * world_rect.zw).r;
	float edge = (vnoise(world_pos.xz * 0.05) - 0.5) * 0.06 + (fine - 0.5) * 0.02;
	float snow = smoothstep(temp - 0.03, temp + 0.03, snow_line + edge);
	snow *= smoothstep(0.6, 0.8, n.y) * smoothstep(0.2, 0.8, world_pos.y);
	// Snow patches in gullies and hollows high in the mountains (they remain all summer)
	float concave = max(smoothstep(0.92, 0.7, ao), m_hollow);
	float alt = smoothstep(135.0, 185.0, world_pos.y + (vnoise(world_pos.xz * 0.02) - 0.5) * 40.0);
	float gully = alt * concave * smoothstep(0.35, 0.55, n.y) * smoothstep(0.55, 0.35, temp + snow_line * 0.3 - 0.1);
	snow = max(snow, smoothstep(0.25, 0.55, gully + (fine - 0.5) * 0.2));
	vec3 snow_col = vec3(0.80, 0.83, 0.90) * (0.92 + fine * 0.12);
	if (snow > 0.01 && tf > 0.001) {
		vec4 sa7; vec3 so7; vec2 sr7;
		layer_sample(7, world_pos, tw, false, warp, 1.0, sa7, so7, sr7);
		snow_col *= mix(vec3(1.0), clamp(sa7.rgb / max(layer_mean[7], vec3(0.004)), vec3(0.5), vec3(1.5)), tf);
		tex_off = mix(tex_off, so7 * tf, snow);
	}
	col = mix(col, snow_col, snow);

	// --- Wet ground (darker and glossier)
	float wet = wetness * (1.0 - snow);
	col *= 1.0 - wet * 0.32;

	// --- Occlusion/curvature
	col *= mix(0.62, 1.0, clamp(ao, 0.0, 1.0)) * (1.0 + max(ao - 1.0, 0.0) * 0.6);

	// --- Bump: grass/soil in the XZ plane, triplanar on steep surfaces
	vec3 bn = n;
	if (near > 0.001) {
		float near_ground = 1.0 - smoothstep(25.0, 90.0, dist);   // fine bump only up close (avoids flicker)
		vec2 g_ground = fbm_grad(world_pos.xz * 0.7, 0.06) * bump_ground * near_ground
		              + fbm_grad(world_pos.xz * 0.08, 0.3) * 0.6;    // large, gentle undulations
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

	ALBEDO = col * cloud_shade(VERTEX, INV_VIEW_MATRIX);
	AO = clamp(ao, 0.0, 1.0) * tex_ao;
	AO_LIGHT_AFFECT = 0.25;
	ROUGHNESS = mix(mix(mix(0.95, 0.8, steep), tex_rough, tf), 0.3, wet * 0.85);
	if (snow > 0.0) ROUGHNESS = mix(ROUGHNESS, 0.7, snow);
	SPECULAR = 0.2 + wet * 0.3;
}
