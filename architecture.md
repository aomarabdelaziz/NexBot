# NexBot architecture

How a Silkroad client bot works, and how NexBot implements it.

## 1. How Silkroad networking works

A Silkroad client talks to **two servers**, one after the other:

```
1) GATEWAY server (login)          2) AGENT server (the game)
   - version check                    - log in with the token
   - server (shard) list              - character list / select
   - username + password              - the world: spawn, move, fight...
   - returns: token + agent IP:port
```

Every TCP connection carries **packets**: `[size:2][opcode:2][count:1][crc:1][payload]`. Before anything else, both
sides run a **security handshake** (opcodes `0x5000` / `0x9000`):

- **Blowfish key exchange**, Diffie-Hellman style. Some packets (login, password) are then Blowfish-encrypted.
- **Count byte + CRC byte**: the client stamps every packet with a sequence byte and a checksum. The server drops
  packets with wrong values.
- After the handshake, each side sends a `0x2001` identity: the server says `"GatewayServer"` or `"AgentServer"`, the
  client says `"SR_Client"`.

To sit between the client and the server, a bot must speak this security layer **on both sides**.

## 2. How a client bot works: the proxy

A bot can't easily read the game's memory, but it can read **all the traffic**. So it puts itself between the client
and the server:

```
 sro_client.exe                    NexBot                        Real server
┌────────────┐   downstream   ┌──────────────────┐   upstream   ┌───────────┐
│ game window│ ─────────────► │ 127.0.0.1:proxy  │ ───────────► │ Gateway / │
│            │ ◄───────────── │ BotSession       │ ◄─────────── │ Agent     │
└────────────┘  (bot = server)└──────────────────┘ (bot=client) └───────────┘
```

- **Upstream**: the bot acts as a *client* towards the real server.
- **Downstream**: the bot acts as a *server* towards the game client.
- Every packet is decrypted, inspected (and changed if needed), re-encrypted and forwarded.

The bot sees everything (HP, mobs, items), and it can **inject** packets (attack, pick up, login) or **drop / rewrite**
packets. RSBot, mBot, sBot and others all work this way.

**Clientless** is the same bot with the downstream side removed. The bot then does what the client used to do, mainly
sending the keep-alive `0x2002` every 5 seconds.

## 3. Making the client connect to the bot: the loader

The client reads the gateway IP from `Media.pk2` (`DivisionInfo.txt`) and connects straight to it. It has to connect
to `127.0.0.1:proxy` instead.

- **RSBot** injects a C++ `Loader.dll` (Microsoft Detours) that hooks `connect()`.
- **NexBot** uses the same idea in pure C#, with no DLL ([ClientLauncher.cs](src/NexBot.Core/Loader/ClientLauncher.cs)):

```
1. Create the mutexes "Silkroad Online Launcher" and "Ready"
   (sro_client refuses to start without a launcher)
2. CreateProcess("sro_client.exe /22 0 0", CREATE_SUSPENDED)
       └─ /locale division gateway = what the real launcher passes
3. CreateRemoteThread → LoadLibraryA("ws2_32.dll") inside the client
       └─ this also runs process init, so the client's DLLs get loaded
4. Find ws2_32!connect in the client and check its first 5 bytes:
       8B FF 55 8B EC   (mov edi,edi / push ebp / mov ebp,esp)
5. Write a small x86 stub into the client and replace those 5 bytes
   with "jmp stub"
6. ResumeThread → the game starts
```

The stub (45 bytes of machine code, built by `ConnectRedirectStub`) runs every time the client calls `connect()`:

```asm
mov  eax, [esp+8]            ; sockaddr* (where to connect)
cmp  family, AF_INET
cmp  port, 15779             ; gateway port?
jne  original                ; no → leave it alone
mov  port, <proxy port>      ; yes → rewrite to the bot
mov  ip,   127.0.0.1
original:
mov edi,edi / push ebp / mov ebp,esp   ; the 5 bytes we overwrote
jmp  connect+5                         ; continue the real connect
```

The app is built as **x86** because the client is 32-bit. A 32-bit bot sees the same `kernel32` / `ws2_32` layout, so
addresses computed in the bot are valid inside the client.

## 4. Logging in without typing: packet tricks

The full login sequence, and what the bot does at each step ([BotSession.cs](src/NexBot.Core/Network/BotSession.cs)):

```
CLIENT                    BOT                              GATEWAY
  │── connect ──────────►│── connect ─────────────────────►│
  │◄═ handshake ════════►│◄═ handshake ═══════════════════►│  (two separate handshakes)
  │── 0x6100 version ───►│────────────────────────────────►│
  │── 0x6101 server list►│────────────────────────────────►│
  │◄─ 0xA101 list ───────│◄────────────────────────────────│
  │                      │── 0x6102 user/pass/shard ──────►│  ★ bot logs in by itself
  │                      │◄─ 0x2322 captcha ───────────────│
  │                      │── 0x6323 answer ───────────────►│  ★ static captcha
  │◄─ 0xA102 (rewritten)─│◄─ 0xA102 token, agent IP:port ──│
  │   IP = 127.0.0.1     │                                 │  ★ client is sent back to the bot
  │   port = proxy       │   remembers the real agent IP
  │
  │   client disconnects and reconnects to the "agent" (= bot)
  │                                                       AGENT
  │── connect ──────────►│── connect to real agent ───────►│
  │── 0x6103 token,"","" ►│── 0x6103 token,user,pass ──────►│  ★ empty credentials replaced
  │◄─ 0xA103 OK ─────────│◄────────────────────────────────│
  │── 0x7007 char list ─►│────────────────────────────────►│
  │◄─ 0xB007 list ───────│◄────────────────────────────────│
  │                      │── 0x7001 "Hero" ───────────────►│  ★ bot selects the character
  │◄─ 0xB001 / 0x3020 ───│◄─ world loads ──────────────────│  → in game
```

The client accepts a login reply it never asked for, because it is just a state machine reacting to packets. That is
the whole "no typing" trick.

Rules in `BotSession`:

- The client's `0x2001` identity is dropped; the upstream security sends its own.
- `0x6100` from the client on the agent connection is dropped (same as RSBot).
- If the user logs in by hand (`0x6102` from the client), the bot does not rewrite `0x6103` and does not answer the
  captcha.
- When the gateway closes during the agent redirect, the client connection is left alone (it is reconnecting).

## 5. Going clientless

- **Go clientless**: drop the downstream connection, kill `sro_client`, keep the upstream agent connection and send
  `0x2002` every 5 s. The bot also answers `0x3020` (spawn) with `0x3012` and `0x34B5` (teleport) with `0x34B6`,
  which the client normally does.
- **Stay online when the client is closed**: if the game window closes while in game, the session switches to
  clientless the same way.
- **Start clientless**: no client and no proxy. The bot runs the whole sequence above itself
  (`0x6100` → `0x6101` → `0x6102` → agent → `0x6103` → `0x7007` → `0x7001`).

## 6. Server config and media (from POC-2)

`Media.pk2` is an encrypted archive (Blowfish, default key `169841`). NexBot reads:

| File inside Media.pk2 | Gives |
|---|---|
| `SV.T` (encrypted) | client version (`188`) |
| `DivisionInfo.txt` | locale (`22`), divisions, gateway IPs |
| `Gateport.txt` | gateway port (`15779`) |
| `textdata\*.txt` | items, skills, mobs, NPCs, regions → `Database.sqlite3` |

The SQLite database turns IDs into names, levels and types. The world tracking needs it (see 7), and so will
later bot features (training, pickup, potions).

## 7. World tracking and the map (from POC-2)

`BotSession` gives every server packet to a `SroClient` (`Sessions/SroClient.cs`) after its own login handling:

```
server packet ─► BotSession.HandleServerPacket (login tricks) ─► forward to the client
                └► Game.HandlePacket ─► PacketHandlerTable ─► WorldState (character, players, mobs, NPCs, drops ...)
```

- The handlers are POC-2's (`Handlers/`): character data `0x3013`, spawns, group spawns, movement, HP/MP, teleports.
  POC-2's login handlers were replaced by `SessionHandlers`, which only track state: the proxy already does the login.
- `SroClient` sends with `BotSession.SendUpstream`, so actions such as `MoveTo` work in client mode as well as
  clientless. The real client does not see packets the bot sends itself.
- `SroClient` is created only when `Database.sqlite3` exists (it needs names and types).

The map (`NexBot.App/Views/MapCanvas.cs`):

- `MapSnapshot.Take(game)` copies what to draw: your position (estimated while moving), status, and markers for
  everything in the same area. It is called from the UI timer (every 250 ms on the map page). The world collections
  are concurrent, so reading them from the UI thread is safe.
- Tiles: each minimap image covers one 192 x 192 sector. Open world files are `minimap\{x}x{y}.ddj` with
  `x = floor(PosX / 192) + 135` and `y = floor(PosY / 192) + 92`. Dungeons use `minimap_d\...\{layer}_{x}x{y}.ddj`
  (`MinimapLayer.For` picks the layer from region and height). `MediaImages` decodes DDJ to PNG on background tasks
  (two at a time); the canvas keeps up to 300 bitmaps.
- Click to walk: `MapSnapshot.WalkTo` sends a move (or a mount move while riding) for points within 1500 units.

The Character and Inventory pages use `CharacterSheet.Take(game)` (`Map/CharacterSheet.cs`, from POC-2's dashboard),
taken every second while one of them is shown. It copies the stats, masteries, skills (the highest level of each
skill line), pets and the items as `ItemView` records. Inventory slots 0-12 are worn, 13 on are the bag. The avatar
inventory is separate. `SlotList` is not a concurrent collection, so a read that collides with a server update is
retried. STR, INT, attack and defense come from `0x303D`, which the server sends after `0x3013`.

The skill window uses `SkillWindow.Take(game)` (`Map/SkillWindow.cs`). The layout comes from SkillData's UI columns
(`UI_SkillColumn` is the line, `UI_SkillRow` the position on it; hidden variants have 255), stored in the skills
table with each level's mastery level, SP and required skills. Masteries come from SkillMasteryData (tab 0/1/2). For
every skill line it finds the learned level, the next level and what that needs; for every mastery the next level's
SP (LevelData `Exp_M`) and the limits (character level; total 330 Chinese, 2 x level European). The buttons call
`LearnSkillAsync` (0x70A1 with the next level's ID) and `RaiseMasteryAsync` (0x70A2). The answers 0xB0A1 / 0xB0A2
and the SP update 0x304E are handled in `Handlers/SkillHandlers.cs`.

`SkillTrainer` (`Map/SkillTrainer.cs`) is created by `BotSession` with the game client and runs on the session's
cancellation token, so it keeps working clientless. Every 3 seconds (0.7 s after a step) it takes a `SkillWindow`
and asks `NextStep` for the cheapest learnable level of a chosen skill line, else the lowest chosen mastery that can
be raised (respecting the gap and the SP to keep). It then calls `LearnSkillAsync` / `RaiseMasteryAsync`. The choices
are `AutoLevelSettings` in `settings.json`; the UI hands an immutable `AutoLevelOptions` copy to the trainer.

Walk scripts (`Automation/`): `WalkScript` parses and writes the text (`MOVE`, `WAIT`, `REPEAT`, `TELEPORT`,
`RETURN`). `ScriptRecorder` polls the character position every 250 ms (a point every N units and where it stops)
and listens to `SroClient.PacketSent`. `BotSession` reports every agent packet the game client sends there, and
`SroClient.Send` reports the bot's own. A `0x705A` (teleport use: unique ID, type 2, destination) becomes a TELEPORT
line with the teleporter's server name (unique IDs change every spawn), and a `0x704C` on a return scroll becomes
RETURN. `AutoWalker` walks MOVE points (re-sending the move when stuck, the mount when riding). For TELEPORT it looks
for the teleporter by server name within 150 units, sends `0x705A`, and waits by the `StateChanged` event for the
loading screen and the return to `InGame`.

Item actions are POC-2's: drag and drop calls `SroClient.MoveItemAsync` and right-click calls `UseItemAsync`. Both wait
for the server's answer, and the page shows the result. Icons come from `MediaImages.GetIcon` (`icon\` + the path in the
items / skills table; the game's default icon when the file is missing). They are decoded in the background
(`Views/IconCache`).

## 8. The UI

WinForms, built in code (no designer). The look is POC-2's Avalonia theme redrawn with GDI+:

| Piece | Class |
|---|---|
| colors, Cinzel font (embedded), noise texture, gradients | `Theme/SroTheme` |
| framed panel with gold title, buttons, input, combo, switch, HP/MP/EXP bar, parchment strip | `Theme/SroControls` |
| header with state pill, status bar | `Views/Chrome` |
| left side menu (Server, Map, Character, Inventory, Log) | `Views/SideNav` |
| map, character card, nearby list | `Views/MapCanvas`, `Views/StatusViews` |
| character page (stats, details, pets) | `Views/CharacterPage` |
| skill window (masteries, skill lines, raise / learn) | `Views/SkillWindowView` |
| inventory page (equipment, bag, item card), item slot | `Views/InventoryPage`, `Views/ItemViews` |
| script page (recorder, editor, path, auto walk) | `Views/ScriptPage` |

Text in Cinzel must be drawn with GDI+ (`Graphics.DrawString`, or `UseCompatibleTextRendering` on labels): GDI
(`TextRenderer`) cannot use fonts loaded from memory.

## 9. Code map

```
NexBot.Core
├─ Protocol/      Packet, Blowfish, SroSecurity        ← from POC-2, plus the server role
├─ Media/         Pk2Archive, ClientInfo, DB builder   ← from POC-2, plus division names
│                 MediaImages (minimap + icons, DDJ → PNG)
├─ Data/          SqliteGameData                       ← from POC-2
├─ Game/          WorldState, entities, items, enums   ← from POC-2
├─ Handlers/      world packet handlers                ← from POC-2 (login handlers replaced)
├─ Sessions/      SroClient (world + actions)          ← adapter around POC-2's client
├─ Automation/    WalkScript, ScriptRecorder, AutoWalker ← POC-2's walker, plus recording and teleports
├─ Map/           MapSnapshot (markers, click-to-walk) ← new, from POC-2's dashboard
│                 CharacterSheet (stats, items, pets)
│                 SkillWindow (masteries, skill lines, states)
│                 SkillTrainer (auto mastery / skill leveling)
├─ Loader/        ClientLauncher, ConnectRedirectStub  ← new (RSBot's idea, pure C#)
├─ Network/       SroConnection (socket + loops)       ← new
│                 BotSession (proxy, auto-login, clientless)
│                 Opcodes, LoginPackets, BotModels
└─ Configuration/ BotSettings (JSON + DPAPI password)  ← new
NexBot.App        WinForms window: MainForm (+ .Layout), Theme/, Views/, UiLogger
tests/            security vs the original API, stub bytes, full login flow with fake servers,
                  world packets → map snapshot and character sheet
```

### Threading

- Each `SroConnection` has a receive loop and a send loop. Packets are delivered one at a time on the receive loop.
- Client packets and server packets arrive on different threads, so shared state in `BotSession` sits behind one lock
  (`_sync`). Sending is thread-safe.
- Events (`StateChanged`, `ShardsReceived`, `CharactersReceived`, `ClientExited`) are raised on those loops; the UI
  marshals them with `BeginInvoke`.
- Never dispose a connection synchronously from inside its own event handler; call `Close()`.

### Security layer notes

- Server role (`SroSecurity.GenerateServerHandshake`): does not stamp count/CRC bytes and does not verify the
  client's. It only encrypts packets that carry the `Encrypted` flag (forwarded packets keep their flag).
- Handshake packets must always be queued ahead of normal packets; normal packets wait until the handshake is
  accepted. (Queuing the handshake reply behind a pending `0x2001` deadlocks; a test covers it.)

## 10. Next steps

All new features plug into `BotSession.HandleServerPacket` / `HandleClientPacket`:

1. ~~Parse world packets into a world state~~ (done: POC-2's handlers, see 7).
2. **Automation loops** (attack, buff, potions, pickup, walk scripts) that read the world state and **inject** packets
   upstream.
3. **Multi-client**: also hook `CreateMutexA` so each client gets a unique "Silkroad Client" mutex, with one
   `BotSession` per client.
4. **Auto reconnect** after a disconnect.
