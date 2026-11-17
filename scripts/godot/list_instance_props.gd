# Shows the properties of an instance of a class (incl. dynamic ones), optionally after setting a property.
# Run: godot --headless --script scripts/godot/list_instance_props.gd -- <Class> [property=value]
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
