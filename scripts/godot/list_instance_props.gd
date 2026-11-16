# Viser egenskapene til en instans av en klasse (inkl. dynamiske), valgfritt etter å ha satt en egenskap.
# Kjør: godot --headless --script scripts/godot/list_instance_props.gd -- <Klasse> [egenskap=verdi]
extends SceneTree

func _init():
	var args := OS.get_cmdline_user_args()
	var obj = ClassDB.instantiate(args[0])
	for a in args.slice(1):
		var kv = a.split("=")
		obj.set(kv[0], str_to_var(kv[1]))
	for p in obj.get_property_list():
		if p.usage & PROPERTY_USAGE_EDITOR or p.usage & PROPERTY_USAGE_STORAGE:
			print(p.name, " : ", type_string(p.type), "  ", p.hint_string.substr(0, 80))
	quit()
