# F.E.A.R. Discord RPC

A simple tool that displays your current level and episode from **F.E.A.R. (first game)** in your Discord status.

## What works

- Only supports **F.E.A.R. Steam version 1.08** (original single-player campaign).
- The program detects:
  - Level filename (e.g., `Intro.World00p`)
  - Episode name (e.g., "Episode 01 – Initiation")
  - Location within the episode (e.g., "Abandoned House")
- Status updates automatically when you change levels.
- Works both in the main menu and during gameplay.

## How to use (for regular users)

1. Download the latest release archive from the **Releases** section.
2. Extract it to any folder.
3. Run **`FearRPC_Packed.exe`**.
4. Launch **F.E.A.R.** (Steam version 1.08).
5. Done — your Discord status will update automatically.

### Configuration files

- **`Settings.ini`** — main settings: Discord application ID, image rotation interval, etc.
- **`LevelDatabase.ini`** — level database: contains episode names, locations, and translations. You can edit this file to fix or add new levels.

### For developers and enthusiasts

If you'd like to improve the tool or adapt it for other game versions — contributions are welcome!

### What can be improved

- **Stable memory address detection**:  
  Currently, the program scans memory for the level string, which may be unreliable.  
  It’s better to find **stable pointer chains or signature patterns** for:
  - Level name
  - Player health
  - Death counter

- **Support for other games**:  
  The code is prepared for expansion (fields like `FEAR2`, `FEAR3` exist), but level reading isn’t implemented yet.  
  To add support:
  - Find offsets/signatures for F.E.A.R. 2 and F.E.A.R. 3
  - Separate episode logic into version-specific databases

- **Better process detection**:  
  Currently uses only the process name (`FEAR.exe`).  
  Can be improved by checking module version, hash, or full path.

- **Robustness and error handling**:  
  Add crash protection when the game closes and improve memory read error handling.

---
# Building the project

You can build and modify the project in **Visual Studio 2022** or **JetBrains Rider**.

- Required environment: **C# language version 9.0**, **.NET Framework 4.8**, **Target platform: x86 (Any CPU with "Prefer 32-bit" enabled)**.
- NuGet dependencies:
  - `DiscordRichPresence` version **1.6.1.70**
  - `Newtonsoft.Json` version **13.0.1**

> The `FearRPC_Packed.exe` in releases is a **merged executable** (via ILMerge) with all libraries embedded.  
> This is for convenience — no external DLLs required.  
> The source is open, and the packing is safe — you can always build your own `.exe` from source.

---
### How to contribute

1. Launch the game and **Cheat Engine** (any version).
2. Find the **memory address of the level string** (e.g., `Intro.World00p`).
3. Perform a **Pointer Scan** to get a stable pointer chain.
4. Update `HEALTH_POINTER_CHAINS` or add a similar structure for level names.
5. Test across different game versions.
6. Submit a Pull Request.

> Note: This tool **does not modify the game** and **does not use cheats** — it only reads memory, just like MSI Afterburner or OBS.

# Important

**Disclaimer:**  
This is a fan project not affiliated with Warner Bros., Monolith Productions, or Discord.  
Use at your own risk. May trigger anti-cheat systems in multiplayer environments.