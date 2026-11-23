# NexBot

Silkroad Online (vSRO 1.188) bot for .NET 10 / WinForms. It works as a **loader**: it starts `sro_client.exe` and logs
it in automatically, so you don't type anything in the game. It can then switch to **clientless**.

## Build & run

```powershell
dotnet build NexBot.sln
dotnet test                                    # protocol, loader stub and login-flow tests
dotnet run --project src\NexBot.App            # the window
```

The app is built as **x86** on purpose. The game client is 32-bit, and the loader patches it from a process of the same bitness.

## Using it

1. **Client folder**: select the folder with `sro_client.exe` and `Media.pk2`, then click **Read server config**. This shows
   the version, locale, gateway port, divisions and gateways from Media.pk2 (`SV.T`, `DivisionInfo.txt`,
   `Gateport.txt`).
2. **Auto login**: username, password, server name (filled from the server list after the first connect), character
   and captcha (if your server uses a static captcha).
3. **Start client**: the game window opens and logs in by itself, then enters the game with the character.
4. **Go clientless**: closes the client; the character stays online (keep-alive every 5 s).
   With "Stay online when the client is closed", closing the game window in game does the same.
5. **Start clientless**: log in without opening the client at all.
6. **Build database**: extracts items/skills/mobs/... from Media.pk2 into `Database.sqlite3` (next to the exe).
   Build it once: the map and the character status need it to track the world.

The pages are in the menu on the left:

- **Server**: client & server config, auto login, bot buttons, a short character card and the activity log.
- **Map**: the minimap from Media.pk2 with players, mobs, uniques, NPCs, pets, drops and teleports around the
  character. Click to walk there (up to 1500 units; the mount walks while riding): a gold line shows the way and the
  distance left, and a faint line to the cursor shows how far a click would go. Drag to look around, use the
  wheel to zoom, right-click to center on yourself. **Nearby** lists everything by distance; double-click an entry to
  walk to it.
- **Character**: name, level, HP / MP / EXP and skill EXP gauges, stats (STR, INT, stat points, attack, defense,
  hit, parry, berserk), gold, SP, job, PvP cape, murders, guild, location, pets, and the **skill window** as in the
  game: Weapon / Force tabs, a tab per mastery, the skill lines with icons and levels. The up button next to the
  mastery level raises it; the up button under a skill learns its next level (hover shows the cost and what is
  missing). Needs a database built by this version ("Build database").
  **Auto leveling:** click **AUTO** on a mastery or right-click skills to choose them, then switch on **Mastery**
  and / or **Skills** in the bar under the window. **Gap** keeps masteries below the character level, **Keep SP**
  keeps a reserve. It works with or without the game client.
  The Pets panel shows the picture of each summoned pet and mount.
- **Inventory**: the equipment around a figure (button for the avatar slots), the bag in pages of 32 slots with item
  icons from Media.pk2, gold, and a card for the hovered item (name, rarity, plus, durability, blue options).
  Drag an item onto another slot to move it, also onto the equipment or avatar slots. Right-click (or double-click)
  to use a potion or scroll, wear equipment, or take it off.
- **Script**: record a route and walk it automatically. Press **Record** and walk in the game (or click the map on
  the page): a point is written every 25 units and where you stop, and teleporters and return scrolls you use are
  written as `TELEPORT` / `RETURN` lines. Edit the text, add `WAIT` lines, save it in `Scripts\`. **Start walking**
  begins at the nearest point, uses the teleports on the way and waits through loading screens.
- **Log**: the full log, optionally with debug messages.

Settings are saved in `settings.json` next to the exe. The password is encrypted with Windows DPAPI, so only your
Windows user can read it. Advanced overrides there: `GatewayHostOverride`, `GatewayPortOverride`, `VersionOverride`,
`LocaleOverride`, `ProxyPort`, `CodePage`, `Pk2Key`, `Executable`.

## How it works

```
sro_client ──► 127.0.0.1:proxy ──(server role)── BotSession ──(client role)──► gateway / agent server
```

- **Loader** ([ClientLauncher](src/NexBot.Core/Loader/ClientLauncher.cs)): same approach as RSBot's `ClientManager`
  (start suspended, inject, resume), but with no native `Loader.dll`. It creates the `Silkroad Online Launcher` / `Ready`
  mutexes, starts the client with `/locale division gateway`, loads `ws2_32.dll` into it on a remote thread and
  patches the hot-patch prologue of `ws2_32!connect` with a 45-byte x86 stub. The stub redirects connections to
  the gateway port to the proxy.
- **Proxy + auto login** ([BotSession](src/NexBot.Core/Network/BotSession.cs)): when the server list arrives the bot
  sends the login (0x6102) and answers the captcha. It rewrites the agent address in 0xA102 to the proxy, replaces the
  client's empty 0x6103 with the real credentials, and joins the character from the 0xB007 list.
- **Protocol** ([SroSecurity](src/NexBot.Core/Protocol/SroSecurity.cs)): from POC-2, with the **server role** added
  (needed to talk to the real client). It is tested against the original SilkroadSecurityApi in `tests/.../Legacy`.
- **Media / database** (`src/NexBot.Core/Media`, `Data`): POC-2's pk2 reader, ClientInfo, GameDatabaseBuilder and
  SqliteGameData.
- **World + map** (`Game`, `Handlers`, `Sessions/SroClient`, `Map`): POC-2's world state and packet handlers. They
  read every server packet that passes through the bot. `MapSnapshot` turns the world into map markers;
  `MediaImages` turns the pk2 minimap images and item icons into PNG.
- **UI** (`src/NexBot.App`): WinForms in POC-2's Silkroad look: black backdrop, metal-framed panels with gold Cinzel
  titles, parchment strips, steel / gold / navy buttons. Every control is drawn in code (`Theme/`, `Views/`).

## Not done yet

- Multi-client (a second client is blocked by the client's own "Silkroad Client" mutex).
- Automatic reconnect after a disconnect.
- Bot features (training, pickup, ...): this version is the loader + login + clientless base.
