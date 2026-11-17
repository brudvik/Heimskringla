shader_type canvas_item;

// Menu background image: covers the screen (like "cover"), zooms perfectly smoothly in UV space
// (no pixel snapping) and is sampled with mipmaps (no flicker when the scale changes).

uniform sampler2D tex : source_color, filter_linear_mipmap;
uniform float zoom = 1.0;          // 1 = whole image, > 1 = closer
uniform vec2 focus = vec2(0.5, 0.48);
uniform float alpha = 1.0;

void fragment() {
	vec2 ts = vec2(textureSize(tex, 0));
	float screen_aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;   // width/height
	float tex_aspect = ts.x / ts.y;
	vec2 uv = UV - 0.5;
	// "cover": fill the screen, crop whatever sticks out
	if (screen_aspect > tex_aspect) uv.y *= tex_aspect / screen_aspect;
	else uv.x *= screen_aspect / tex_aspect;
	uv = uv / zoom + focus;
	vec3 c = texture(tex, uv).rgb;
	COLOR = vec4(c, alpha);
}
