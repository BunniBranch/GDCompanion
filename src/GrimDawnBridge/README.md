# Grim Dawn native bridge

This x64 DLL is loaded only when the user presses **Connect to game**. It resolves
the two required `Engine.dll` exports by decorated name, hooks the Lua update loop
with MinHook, and accepts local commands through a per-process named pipe.

It does not hide itself, persist, access the network, or bypass anti-cheat. The
project is intended only for local/single-player use.
