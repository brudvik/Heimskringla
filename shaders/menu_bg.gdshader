shader_type canvas_item;

// Bakgrunnsbildet i menyen: dekker skjermen (som "cover"), zoomer helt jevnt i UV-rommet
// (ingen piksel-festing) og samples med mipmaps (ingen flimring når skalaen endres).

uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float zoom = 1.0;          // 1 = hele bildet, > 1 = nærmere
uniform vec2 focus = vec2(0.5, 0.48);
uniform float alpha = 1.0;

void fragment() {
	vec2 ts = vec2(textureSize(tex, 0));
	float screen_aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;   // bredde/høyde
	float tex_aspect = ts.x / ts.y;
	vec2 uv = UV - 0.5;
	// "cover": fyll skjermen, beskjær det som stikker utenfor
	if (screen_aspect > tex_aspect) uv.y *= tex_aspect / screen_aspect;
	else uv.x *= screen_aspect / tex_aspect;
	uv = uv / zoom + focus;
	vec3 c = texture(tex, uv).rgb;
	COLOR = vec4(c, alpha);
}
