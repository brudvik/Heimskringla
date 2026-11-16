shader_type spatial;
render_mode cull_disabled, depth_draw_always, specular_schlick_ggx, ambient_light_disabled;

// Vann for hav, innsjøer og elver.
//  - Havet (is_ocean): Gerstner-bølger i vertex-shaderen, styrt av vinden, dempet på grunt vann
//    (dybden slås opp i world_map) og tonet ut på avstand. Brenninger/skum mot land.
//  - Elver/innsjøer: småkrusninger som renner med strømmen (flowmap fra world_map.ba),
//    hvitt skum der det går fort.

#include "res://shaders/wind.gdshaderinc"

uniform vec3 shallow_color : source_color = vec3(0.20, 0.50, 0.55);
uniform vec3 deep_color : source_color = vec3(0.03, 0.14, 0.24);
uniform float wave_strength = 1.0;     // småkrusninger
uniform bool is_ocean = false;
uniform float wave_height = 1.0;       // skalerer Gerstner-bølgene
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform float reflect_strength = 1.0;
global uniform vec3 sky_horizon;
global uniform vec3 sky_zenith;
global uniform vec3 cloud_light;
global uniform vec3 cloud_shadow;
global uniform vec4 cloud_params;    // dekke, mørke, natt
global uniform vec2 cloud_offset;
global uniform sampler2D world_map : filter_linear;
global uniform vec4 world_rect;
global uniform float wetness;

varying vec3 world_pos;
varying vec3 wave_normal;
varying float crest;
varying float ground_h;
varying float cam_dist;

// ---------------------------------------------------------------- støy (heltalls-hash)
float hash(vec2 p) {
	uvec2 q = uvec2(ivec2(floor(p)) + ivec2(65536));
	uint h = q.x * 1597334677u ^ q.y * 3812015801u;
	h ^= h >> 16u; h *= 2246822519u; h ^= h >> 13u; h *= 3266489917u; h ^= h >> 16u;
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
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

// Gradient av krusningsfeltet (to oktaver gradientstøy, endelig differanse)
vec2 ripple_grad(vec2 p) {
	p = mod(p, 1024.0);
	float e = 0.05;
	float h0 = gnoise(p) + gnoise(p * 2.7 + 13.0) * 0.45;
	float hx = gnoise(p + vec2(e, 0)) + gnoise((p + vec2(e, 0)) * 2.7 + 13.0) * 0.45;
	float hz = gnoise(p + vec2(0, e)) + gnoise((p + vec2(0, e)) * 2.7 + 13.0) * 0.45;
	return vec2(hx - h0, hz - h0) / e;
}

// ---------------------------------------------------------------- Himmel med skyer (kopi av sky.gdshader)
float sky_hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}
float sky_vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(sky_hash(i), sky_hash(i + vec2(1, 0)), u.x), mix(sky_hash(i + vec2(0, 1)), sky_hash(i + vec2(1, 1)), u.x), u.y);
}
float sky_fbm(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 5; i++) {
		s += sky_vnoise(p) * a;
		p = p * 2.02 + vec2(5.3, 1.7);
		a *= 0.5;
	}
	return s;
}
vec3 sky_color(vec3 d) {
	float up = clamp(d.y, 0.0, 1.0);
	vec3 col = mix(pow(sky_horizon, vec3(2.2)), pow(sky_zenith, vec3(2.2)), pow(up, 0.55));
	if (d.y > 0.0) {
		vec2 uv = d.xz / (d.y + 0.12) * 1.4 + cloud_offset;
		float cc = cloud_params.x, dark = cloud_params.y;
		float c = sky_fbm(uv);
		float cov = smoothstep(1.0 - cc - 0.12, 1.0 - cc + 0.28, c);
		cov = max(cov, smoothstep(0.75, 1.0, cc));
		float thick = sky_fbm(uv * 2.0 + 7.0);
		vec3 ccol = mix(pow(cloud_shadow, vec3(2.2)), pow(cloud_light, vec3(2.2)), clamp(0.35 + thick * 0.6, 0.0, 1.0));
		ccol *= mix(1.0, 0.45 + thick * 0.3, dark);
		ccol = mix(ccol, ccol * vec3(0.12, 0.13, 0.18), cloud_params.z);
		col = mix(col, ccol, cov * smoothstep(0.0, 0.15, d.y) * 0.92);
	}
	return col;
}

// ---------------------------------------------------------------- Gerstner
// Én bølge: retning d, bølgelengde L, steilhet q, amplitude a. Legger til forskyvning og normal-bidrag.
void gerstner(vec2 p, vec2 d, float L, float a, float q, float t, inout vec3 disp, inout vec3 nrm) {
	float k = 6.2831853 / L;
	float c = sqrt(9.81 / k);                  // fasehastighet (dypt vann)
	float f = k * (dot(d, p) - c * t);
	float s = sin(f), co = cos(f);
	disp.x += q * a * d.x * co;
	disp.z += q * a * d.y * co;
	disp.y += a * s;
	nrm.x -= d.x * k * a * co;
	nrm.z -= d.y * k * a * co;
	nrm.y -= q * k * a * s;
}

vec2 rot(vec2 v, float a) { float c = cos(a), s = sin(a); return vec2(c * v.x - s * v.y, s * v.x + c * v.y); }

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	cam_dist = distance(world_pos.xz, cam.xz);
	ground_h = texture(world_map, (world_pos.xz - world_rect.xy) * world_rect.zw).g * 100.0;
	wave_normal = vec3(0.0, 1.0, 0.0);
	crest = 0.0;
	if (is_ocean) {
		float depth = max(world_pos.y - ground_h, 0.0);
		float shoal = smoothstep(0.0, 12.0, depth);                  // mindre bølger på grunt vann
		float far = 1.0 - smoothstep(350.0, 1300.0, cam_dist);
		float sea = (0.25 + wind_strength * 1.1) * wave_height;      // sjøgang etter vinden
		float amp = sea * mix(0.35, 1.0, shoal) * far;
		vec2 wd = normalize(wind_dir + vec2(0.0001));
		vec3 disp = vec3(0.0);
		vec3 n = vec3(0.0, 1.0, 0.0);
		float t = TIME;
		vec2 p = world_pos.xz;
		gerstner(p, wd,                 38.0, 0.38 * amp, 0.55, t, disp, n);
		gerstner(p, rot(wd, 0.45),      23.0, 0.22 * amp, 0.6,  t, disp, n);
		gerstner(p, rot(wd, -0.6),      15.0, 0.14 * amp, 0.6,  t, disp, n);
		gerstner(p, rot(wd, 1.1),        9.0, 0.07 * amp, 0.5,  t, disp, n);
		gerstner(p, rot(wd, -1.3),       6.0, 0.045 * amp, 0.5, t, disp, n);
		gerstner(p, rot(wd, 0.2),       61.0, 0.5 * amp, 0.35,  t, disp, n);
		VERTEX += disp;
		world_pos += disp;
		wave_normal = normalize(n);
		crest = clamp(disp.y / max(amp * 0.9, 0.05), 0.0, 1.0);
	}
}

void fragment() {
	float dist = length(VERTEX);
	float fade = 1.0 - smoothstep(60.0, 450.0, dist);

	// Strøm (elver): world_map.ba = fart i x/z (m/s)
	vec2 flow = vec2(0.0);
	if (!is_ocean) flow = texture(world_map, (world_pos.xz - world_rect.xy) * world_rect.zw).ba;
	float speed = length(flow);

	// Flowmap: to faser som glir langs strømmen og blandes, så mønsteret ikke strekkes i det uendelige
	float cyc = 1.6;
	float ph0 = fract(TIME / cyc), ph1 = fract(TIME / cyc + 0.5);
	float wgt = abs(1.0 - 2.0 * ph0);
	vec2 rp = world_pos.xz * 0.9;
	// Vinden lager krusninger som vandrer med den på stille vann
	vec2 drift = wind_dir * TIME * (0.3 + wind_strength * 1.2);
	vec2 g0 = ripple_grad(rp - flow * ph0 * cyc * 0.9 - drift);
	vec2 g1 = ripple_grad(rp - flow * ph1 * cyc * 0.9 - drift + vec2(37.0, 11.0));
	vec2 g = mix(g1, g0, wgt);
	float rip = wave_strength * (0.18 + wind_strength * 0.5 + min(speed, 2.0) * 0.25) * fade;
	vec3 n_world = normalize(wave_normal + vec3(-g.x, 0.0, -g.y) * rip * 0.35);
	NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);

	// Dybde (fra dybdebufferen): hvor mye vann vi ser gjennom
	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	vec3 pixel_pos = upos.xyz / upos.w;
	float water_depth = max(0.0, VERTEX.z - pixel_pos.z);

	// ---- Lysbrytning: bunnen sett gjennom vannet, forvrengt av bølgene
	vec2 ruv = SCREEN_UV + n_world.xz * 0.035 * clamp(water_depth * 0.3, 0.0, 1.0) * fade;
	float rd = texture(depth_tex, ruv).r;
	vec4 rpos = INV_PROJECTION_MATRIX * vec4(ruv * 2.0 - 1.0, rd, 1.0);
	float rdepth = VERTEX.z - rpos.z / rpos.w;
	if (rdepth < 0.0) { ruv = SCREEN_UV; rdepth = water_depth; }   // ikke la ting foran vannet "lekke" inn
	vec3 under = textureLod(screen_tex, ruv, 0.0).rgb;
	// Lys absorberes: rødt først, så grønt (klart nordisk vann blir grønnblått)
	vec3 trans = exp(-max(rdepth, 0.0) * vec3(0.42, 0.11, 0.075) * (is_ocean ? 1.0 : 1.4));
	float dt = clamp(water_depth / 9.0, 0.0, 1.0);
	vec3 body = mix(shallow_color, deep_color, dt);
	body += vec3(0.02, 0.09, 0.08) * crest * (1.0 - dt * 0.5);   // lys gjennom bølgetoppene

	// Skum: langs land, på bølgetopper, i stryk
	float fn = vnoise(world_pos.xz * 1.7 - flow * TIME * 1.2) * 0.6 + vnoise(world_pos.xz * 5.3 - flow * TIME * 1.6) * 0.4;
	float shore = 1.0 - smoothstep(0.0, 0.18 + wind_strength * 0.25, water_depth);
	float foam = shore * smoothstep(0.5, 0.8, fn + shore * 0.15) * 0.8;
	if (is_ocean) {
		// Brenninger: skumlinjer som ruller inn mot land på grunt vann
		float gd = max(world_pos.y - ground_h, 0.0);
		float surf = (1.0 - smoothstep(0.3, 1.5 + wind_strength * 2.5, gd)) * smoothstep(0.0, 0.3, gd);
		float bands = smoothstep(0.75, 0.95, sin(gd * 2.2 + TIME * 1.4 + fn * 2.0) * 0.5 + 0.5);
		foam = max(foam, surf * bands * smoothstep(0.35, 0.7, fn) * (0.4 + wind_strength));
		foam = max(foam, smoothstep(0.65, 0.95, crest) * smoothstep(0.45, 0.75, fn) * smoothstep(0.35, 0.8, wind_strength) * fade);
	} else {
		foam = max(foam, smoothstep(1.4, 2.8, speed) * smoothstep(0.55, 0.85, fn) * 0.8);
	}
	foam = clamp(foam, 0.0, 1.0);

	// ---- Speiling: skjermrom-sporing mot dybdebufferen (fjell, trær, strand), ellers himmel
	vec3 rv = reflect(-VIEW, NORMAL);                  // visningsrom
	vec3 rw = (INV_VIEW_MATRIX * vec4(rv, 0.0)).xyz;
	rw.y = abs(rw.y);
	vec3 sky = sky_color(normalize(rw));
	vec3 refl = sky;
	float hit = 0.0;
	if (reflect_strength > 0.0 && rv.z < 0.2) {
		vec3 p = VERTEX;
		float stp = 0.6 + length(VERTEX) * 0.012;
		vec2 huv = vec2(0.0);
		for (int i = 0; i < 40; i++) {
			p += rv * stp;
			stp *= 1.12;
			vec4 cp = PROJECTION_MATRIX * vec4(p, 1.0);
			vec2 suv = cp.xy / cp.w * 0.5 + 0.5;
			if (suv.x < 0.0 || suv.x > 1.0 || suv.y < 0.0 || suv.y > 1.0 || cp.w < 0.0) break;
			float sd = texture(depth_tex, suv).r;
			vec4 sp = INV_PROJECTION_MATRIX * vec4(suv * 2.0 - 1.0, sd, 1.0);
			float sz = sp.z / sp.w;
			float diff = sz - p.z;                     // > 0: strålen er bak overflaten
			if (diff > 0.0 && diff < stp * 2.5 + 1.0) {
				// Finjuster treffet (binærsøk)
				vec3 a = p - rv * stp / 1.12, b = p;
				for (int k = 0; k < 5; k++) {
					vec3 m = (a + b) * 0.5;
					vec4 mp = PROJECTION_MATRIX * vec4(m, 1.0);
					vec2 muv = mp.xy / mp.w * 0.5 + 0.5;
					vec4 mq = INV_PROJECTION_MATRIX * vec4(muv * 2.0 - 1.0, texture(depth_tex, muv).r, 1.0);
					if (mq.z / mq.w > m.z) b = m; else a = m;
				}
				vec4 bp = PROJECTION_MATRIX * vec4(b, 1.0);
				huv = bp.xy / bp.w * 0.5 + 0.5;
				vec2 edge = smoothstep(vec2(0.0), vec2(0.08), huv) * smoothstep(vec2(1.0), vec2(0.92), huv);
				hit = edge.x * edge.y * (1.0 - float(i) / 40.0 * 0.5);
				break;
			}
		}
		if (hit > 0.0) refl = mix(sky, textureLod(screen_tex, huv, 0.0).rgb, hit);
	}

	float fres = 0.02 + 0.98 * pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 5.0);
	fres = clamp(fres * 1.15, 0.0, 1.0) * (1.0 - foam);
	// Sluttfarge: bunnen (gjennom vannet) + spredt lys i vannet + speiling
	float scatter = 1.0 - (trans.r + trans.g + trans.b) / 3.0;
	vec3 lit_body = body * scatter;                     // belyses av sol/himmel (ALBEDO)
	ALBEDO = mix(lit_body * (1.0 - fres), vec3(0.9, 0.95, 1.0), foam * 0.85);
	// Omgivelseslys (motorens er slått av for vannet, så himmelen ikke speiles to ganger)
	vec3 amb = pow(mix(sky_horizon, sky_zenith, 0.5), vec3(2.2));
	EMISSION = under * trans * (1.0 - foam) * (1.0 - fres) + refl * fres * reflect_strength + ALBEDO * amb * 0.6;
	if (wetness > 0.05) {
		vec2 cell = floor(world_pos.xz * 3.0);
		float ph = fract(TIME * 1.3 + hash(cell) * 7.0);
		vec2 c = (cell + 0.25 + vec2(hash(cell + 3.1), hash(cell + 7.7)) * 0.5) / 3.0;
		float r = distance(world_pos.xz, c) * 3.0;
		float ring = smoothstep(0.05, 0.0, abs(r - ph * 0.5)) * (1.0 - ph) * step(hash(cell + 1.7), wetness);
		ALBEDO += vec3(0.25) * ring * fade;
	}
	// Myk overgang mot land (ingen skarp kant)
	ALPHA = clamp(smoothstep(0.0, 0.25, water_depth) + foam, 0.0, 1.0);
	ROUGHNESS = mix(0.04 + wind_strength * 0.05, 0.6, foam);
	METALLIC = 0.0;
	SPECULAR = 0.5;
}
