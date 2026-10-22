# Furniture Overlap

A Tailside mod for overlapping furniture, placing decorations freely, and adjusting their draw order.
Downloads, installation instructions, and controls will be available on Steam Workshop. A link will be added here when published.

## Building

Requires the .NET SDK, PowerShell, Tailside, and BepInEx 5 for Unity Mono.
Run from the repository root with your game installation path:

```powershell
.\Build.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Tailside'
```

Build output is written to `release/`. Game and BepInEx DLLs are not included.

The development tests in `tests/` require an isolated game copy with save isolation and separately prepared test data.

## License

My original code and documentation are licensed under [0BSD](https://spdx.org/licenses/0BSD).
Game images and third-party libraries retain their respective rights and licenses.
