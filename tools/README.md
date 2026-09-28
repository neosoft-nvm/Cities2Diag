# tools

Put the PresentMon **console** executable here; the monitor finds `tools\PresentMon*.exe` automatically.

Tested with PresentMon 2.6.0:

* Download `PresentMon-2.6.0-x64.exe` (~1 MB) from the official releases:
  https://github.com/GameTechDev/PresentMon/releases
* Check it is signed by Intel Corporation (Properties → Digital Signatures).
  SHA-256 of 2.6.0: `B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF`

Not the `.msi` — that installs the full PresentMon GUI overlay, which is not needed.

The exe is git-ignored (third-party binary).

## SaveInspector (which mods one save uses)

Double-click `SaveInspector.cmd` (or run `SaveInspector.ps1`), pick a save, and it lists:

* asset packs you have enabled that this save does **not** use (largest first, with any other city that uses them),
* mods the save uses, and mods it needs that are not enabled (missing content),
* code mods, which a save does not record and are never suggested.

It reads only the small metadata entry inside the save; the game does not need to be running and nothing is changed.
Results go to the Desktop (`cs2_save_mods_<city>.csv`, `cs2_save_unused_<city>.txt`); the unused list is also copied to
the clipboard for building a playset in Skyve. `AssetAudit.ps1` does the same check across all saves at once.

## LeanPlayset (Skyve playset for one save)

`LeanPlayset.ps1 -Save <path to .cok>` writes `<City> lean playset.json` (Skyve's playset export format) to the Desktop:
your currently enabled mods minus the asset packs that save does not use. Code mods and packs under 2 MB are always kept.
Import it in Skyve (Playsets → Import), activate it and start the game from Skyve. Read-only for the game.
