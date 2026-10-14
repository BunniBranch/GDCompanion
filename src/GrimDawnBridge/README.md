# Grim Dawn native bridge

The matching x64 or x86 DLL is loaded only when the user presses **Connect to
game**. Each resolves the required `Engine.dll` exports by decorated name, hooks
the Lua update loop with MinHook, and accepts local commands through a per-process
named pipe.

The x86 build is intentionally minimal: it exposes synchronous Lua execution and
clean unload for ordinary item and blueprint spawning. Architecture-dependent
affixes, navigation, and character utilities are omitted until their x86 layouts
are independently verified. It never reuses x64 offsets.

It does not hide itself, persist, access the network, or bypass anti-cheat. The
project is intended only for local/single-player use.
