shader_type sky;

// Himmel med gradient, sol, drivende skyer og stjerner om natta. Styres fra DayNight.cs.

uniform vec3 top_color : source_color = vec3(0.28, 0.50, 0.84);
uniform vec3 horizon_color : source_color = vec3(0.68, 0.78, 0.88);
uniform vec3 ground_color : source_color = vec3(0.30, 0.32, 0.30);
uniform vec3 sun_color : source_color = vec3(1.0, 0.95, 0.85);
uniform vec3 cloud_light : source_color = vec3(1.0, 1.0, 1.0);
uniform vec3 cloud_shadow : source_color = vec3(0.55, 0.6, 0.7);
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);   // retning mot sola
uniform float cloud_coverage = 0.45;
uniform float night = 0.0;                   // 0 = dag, 1 = natt
uniform vec3 moon_dir = vec3(0.0, -1.0, 0.0);
uniform float aurora = 0.0;                  // nordlysstyrke 0..1
uniform float cloud_dark = 0.0;              // 0 = hvite skyer, 1 = tunge regnskyer
uniform vec2 wind_offset = vec2(0.0);        // skyforskyvning (driver med vinden)

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

float fbm(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 5; i++) {
		s += vnoise(p) * a;
		p = p * 2.02 + vec2(5.3, 1.7);
		a *= 0.5;
	}
	return s;
}

// Nordlys: gardiner langt nord (-Z), bygget opp av lag i ulike høyder langs synslinja.
vec3 aurora_light(vec3 d, float t) {
	if (d.y < 0.01) return vec3(0.0);
	vec3 acc = vec3(0.0);
	const int LAYERS = 28;
	for (int i = 0; i < LAYERS; i++) {
		float f = float(i) / float(LAYERS);
		float h = 1.0 + f * 1.6;                       // lagets høyde (relativ)
		vec2 p = d.xz / d.y * h;                       // treffpunkt i laget
		// Gardinens bølgende linje (z-posisjon som funksjon av x)
		float line = -3.2 + sin(p.x * 0.28 + t * 0.04) * 0.9 + sin(p.x * 0.71 - t * 0.07) * 0.35 + sin(p.x * 1.9 + t * 0.15) * 0.08;
		float dz = p.y - line;
		float curtain = exp(-dz * dz * 3.5);
		// Loddrette stråler langs gardinen
		float rays = vnoise(vec2(p.x * 7.0 + t * 0.25, t * 0.05)) * 0.7 + vnoise(vec2(p.x * 23.0 - t * 0.6, 3.0)) * 0.3;
		rays = pow(rays, 2.2) * 2.4;
		// Grønt nederst, mot fiolett/rødt øverst; nederkanten er skarpest
		vec3 c = mix(vec3(0.15, 1.0, 0.45), vec3(0.55, 0.25, 0.85), smoothstep(0.35, 1.0, f));
		float fade = (1.0 - f) * (1.0 - f) * smoothstep(0.0, 0.08, f + 0.02);
		acc += c * curtain * rays * fade;
	}
	float horizon_fade = smoothstep(0.02, 0.15, d.y);
	return acc / float(LAYERS) * 2.8 * horizon_fade;
}

void sky() {
	vec3 d = normalize(EYEDIR);
	float up = clamp(d.y, 0.0, 1.0);
	vec3 col = mix(horizon_color, top_color, pow(up, 0.55));
	if (d.y < 0.0) col = mix(horizon_color, ground_color, clamp(-d.y * 4.0, 0.0, 1.0));

	float sd = max(dot(d, normalize(sun_dir)), 0.0);
	float sun_vis = smoothstep(-0.12, 0.02, sun_dir.y);
	col += sun_color * (pow(sd, 6.0) * 0.18 + pow(sd, 120.0) * 0.8) * sun_vis;

	if (!AT_CUBEMAP_PASS) {
		// Sol-skive
		col += sun_color * smoothstep(0.9993, 0.99965, sd) * 25.0 * sun_vis;

		// Stjerner
		if (night > 0.01 && d.y > 0.0) {
			vec2 sp = d.xz / (d.y + 0.2) * 220.0;
			float st = step(0.9975, hash(floor(sp))) * (0.5 + 0.5 * sin(TIME * 2.0 + hash(floor(sp) + 3.0) * 30.0));
			col += vec3(st) * night * smoothstep(0.0, 0.2, d.y);
		}

		// Måne
		float md = max(dot(d, normalize(moon_dir)), 0.0);
		if (night > 0.01) {
			float disc = smoothstep(0.99955, 0.99975, md);
			vec2 mp = (d - normalize(moon_dir) * md).xy * 900.0;
			float maria = vnoise(mp * 0.9) * 0.5 + vnoise(mp * 2.3) * 0.25;
			col += vec3(0.95, 0.93, 0.85) * disc * (1.4 - maria * 0.8) * night * 2.5;
			col += vec3(0.5, 0.6, 0.8) * pow(md, 300.0) * 0.25 * night;   // glorie
		}

		// Nordlys
		if (aurora > 0.001) col += aurora_light(d, TIME) * aurora * night;

		// Skyer: projiser på et skyplan
		if (d.y > 0.0) {
			vec2 uv = d.xz / (d.y + 0.12) * 1.4 + wind_offset;
			float c = fbm(uv);
			float cov = smoothstep(1.0 - cloud_coverage - 0.12, 1.0 - cloud_coverage + 0.28, c);
			cov = max(cov, smoothstep(0.75, 1.0, cloud_coverage));   // helt overskyet
			float thick = fbm(uv * 2.0 + 7.0);
			vec3 ccol = mix(cloud_shadow, cloud_light, clamp(0.35 + thick * 0.6 + pow(sd, 4.0) * 0.4 * (1.0 - cloud_dark), 0.0, 1.0));
			ccol *= mix(1.0, 0.45 + thick * 0.3, cloud_dark);
			ccol = mix(ccol, ccol * vec3(0.12, 0.13, 0.18), night);
			float fade = smoothstep(0.0, 0.15, d.y);
			col = mix(col, ccol, cov * fade * 0.92);
		}
	}
	COLOR = col;
}
