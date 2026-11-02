# Capy Bara's Server Manager

A capybara-and-orange Windows app for hosting MX Simulator sessions with friends. Server configuration, track rotations, live administration, router setup, and the **Create session / Join session** network are inside the manager. No Radmin, separate VPN client, standalone relay tool, VPN driver, or system route changes are required.

**[Download the Windows app v1.1.1](https://github.com/gsshunter-models/capy-baras-server-manager/releases/download/v1.1.1/CapyBaraServerManager-v1.1.1-windows-portable.zip)** · [Release page](https://github.com/gsshunter-models/capy-baras-server-manager/releases/tag/v1.1.1)

Extract the complete ZIP and run **CapyBaraServerManager.exe**. Requires Windows and the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0). MX Simulator is separate. Hosting uses your existing **mxserver.exe** folder; the native server and game/track files are not redistributed in this repository. Joining only requires the manager and game.

## Automatic setup and a name in the game

Open **Connect**, use **Open DuckDNS** to sign in and claim a free name once, then enter that name and your account token. **Save name & token** encrypts the token for your Windows account and this profile. It is stored separately from profile JSON and is not copied into room invites. The manager cannot create an authenticated DuckDNS account or claim a name on your behalf without that one-time account step.

Press **Set up & host**. The app configures the default game port, requests its scoped firewall rule (Windows can ask for permission), requests router forwarding where needed, updates the DuckDNS address and starts the server. DNS is refreshed every five minutes while hosting so changes to the public address can be picked up. Friends can type **your-name.duckdns.org** directly into MX Simulator; no port number is needed. Use **Copy game address** for that plain hostname. The in-app room invite remains available separately.

For a public room backend, use **Automatic relay setup** on Run relay after saving its DuckDNS name/token. It configures the backend name, game/tunnel ports, firewall and router mapping, then starts the in-app relay. It also pairs the local host automatically; on a different PC, paste the backend's private host pairing once. The private game host can then share the backend's hostname for ordinary in-game joining, even when that host is behind CGNAT. The public backend itself must have incoming connectivity. No hosted relay infrastructure is provisioned by this app.

If automatic setup cannot acquire the default game port, the router blocks automatic mappings, the ISP uses CGNAT, or DuckDNS rejects the account/name, the app reports that step instead of claiming the server is ready. An already assigned router mapping is left unchanged. First-time DNS changes can take time to resolve, and a friend should test the connection from outside your LAN. DNS naming does not bypass CGNAT. Existing advanced manual controls remain available by turning off automatic setup.

References: [DuckDNS API](https://www.duckdns.org/spec.jsp), [MX Simulator's default-port hostname behavior](https://forum.mxsimulator.com/viewtopic.php?p=35122).

## Create and join sessions

The host chooses a profile and tracks, then presses **Set up & host**. Turn off automatic setup to use the existing manual networking controls. **Connect** contains the session invite and join controls. A friend pastes the invite into **Join session**. If the game is closed, the manager launches it and connects it. If it is already open, the manager copies the address for the game's Connect screen and leaves the current session alone. Keep the manager open while using its room network.

- **Direct hosting:** the app starts mxserver and can request a UPnP/NAT-PMP UDP mapping from the router. LAN users use the LAN address; outside users need the public mapped address. Port mappings renew while hosting and are removed on stop where supported. Existing UPnP mappings are not overwritten.
- **Integrated room network:** host and player tunnels are implemented inside this app. A `capyroom://` invite lets the player's manager carry native game UDP packets through its own pinned TLS connection to the room backend. The game connects to a loopback endpoint created by the manager. Both host and friend can make outbound connections; they do not install a VPN or run another networking executable.
- **Public room backend:** on a publicly reachable PC or Windows VPS, open this same app's **Run relay** tab. Choose its public hostname and ports, allow its firewall rules, and start the relay. The app can request router mappings here too. Give the private host pairing code only to the mxserver host. On that host's Connect tab, enable the integrated room network and paste the private pairing. Create the session, then share its player room invite. A public PC can run the relay and native game host in the same manager.

There is **no hosted relay service included or automatically provisioned**. CGNAT or blocked incoming ports require a reachable backend machine. Running relay code on another blocked connection cannot make it public. Router mapping success is not proof of outside connectivity; test with a friend on another network. The manager reports disconnected tunnels rather than presenting them as connected. TLS/TCP relay traffic can add latency and head-of-line delay compared with direct UDP.

The relay's TCP port carries the in-app host/player tunnels. Its UDP port also supports ordinary native clients. Only the relay machine needs inbound reachability for room mode. Its default ports are UDP 19801 and TCP 19802. The game host's default UDP port is 19800. Firewall buttons use a scoped Windows elevation helper; they add/remove only the manager's named port rules. The app never disables the firewall.

Player room codes authorize game traffic, not private host control. The private host key and generated native admin-control secret are separate. Treat room invites as invitations to your session and share the host pairing only with the host. Relay identity files and local profiles contain connection secrets; do not publish the manager's local data folder.

## Settings and administration

Save multiple named profiles or duplicate one to create another server. The first run can import `serverargs.txt` from a `mxserver` folder on the Desktop, and reads a numeric ADMINID from its batch file without executing the batch. Other folders can be selected. Import saved argument files through the sidebar; nested args files are not followed.

Host settings include port, bind address, admin UID, erode, maximum clients/riders, outgoing rate, greeting, and default laps, minutes and track interval. The track rotation supports enabled tracks, ordering, per-track laps/minutes/interval, and zero-based next-track links. The advanced grid exposes the remaining options supported by the inspected native server: bans, demos, JavaScript, extensions, inactivity timeout, lineup/results output, marshals, reserved clients/UIDs, track cuts, minimum version, arcade restrictions, off-course settings, administrator password, results commands, sendfile prefix, update limit, network test values and redirects. The native xcmd secret is generated and managed internally. Custom executable server options require an explicit start confirmation.

Configuration changes apply at the next server start. Live controls send native local admin commands for erode, restart, next/selected track, lock/unlock, player kick, force play/spectate, mute, and a one-line command box for other native admin commands. Switching tracks queues the selected running track and requests a race restart. Tracks added/reordered after startup require a server restart. The live player view shows slot, UID, state, rank and ping. The native server initializes race/terrain information when riders connect, so an empty server can report that it is waiting for its first rider.

The **password** native option is an administrator password, not a player join password. Native reserved slots/UID options are available when you want to restrict access. The app catalog targets the supplied native server options; unsupported options in an imported file are reported instead of silently discarded.

## Trackinfos from the game

The Tracks tab scans the server's trackinfo folder, the chosen MX Simulator personal folder, and the manager's imported library. Search names, multi-select tracks, add them to the rotation and reorder them. Import individual trackinfo files in a batch as well.

To create a missing trackinfo, load that track in MX Simulator and send **`/savetrackinfo`** in chat. The app has a copy button and watches the personal folder for new exports. Valid exports are copied into its deduplicated library; game files are left in place. It does not fabricate track hashes or claim to generate native trackinfos from unavailable/protected content. Everyone still needs matching track files installed in the game.

## Files and lifecycle

Local data: `%LOCALAPPDATA%\CapyBaraServerManager`. Profiles, imports, backend identity, bans and session files stay here. Native session args, status helper, copied rotation trackinfos, results and lineup output are generated under that profile's session folder. Relative output options are kept in managed storage. Original server args, batch files and trackinfos remain in their original folder. No hooks, patches or OpenGL replacement are used.

The app starts only its selected native server executable, using a generated args file without a shell. Stop sends the native shutdown command. Windows job ownership prevents its child server being orphaned if the manager crashes. Closing asks before ending active hosted/joined sessions. The manager does not stop unrelated servers or games. A failed graceful stop is reported.

## Build and checks

```powershell
dotnet run --project Tests/CapyServer.Tests.csproj -c Release -p:BuildInParallel=false
dotnet publish Desktop/CapyServer.Desktop.csproj -c Release --no-self-contained -o artifacts/CapyBaraServerManager
```

An optional read-only native integration check uses `-- --native PATH_TO_MXSERVER_FOLDER` on the tests. It binds a temporary server only to loopback, copies trackinfos into disposable session storage, checks native status and admin-command acceptance, then shuts it down. It does not open router or firewall ports. Synthetic checks cover argument safety/order, exact imports, profile independence, invite validation, separate host/player authority, TLS certificate rejection, multiple UDP clients, in-app player bridges, 8 KB packet preservation, a player leaving, and port cleanup. Router-specific behavior and real internet latency still require testing on the target networks.

Networking uses [Mono.Nat 3.0.4](https://github.com/mono/Mono.Nat) under its included MIT notice. Windows credential checks: `dotnet run --project Tests.Windows/CapyServer.WindowsTests.csproj -c Release -p:BuildInParallel=false`. These check encrypted token roundtrips, profile isolation and exclusion from profile JSON. DuckDNS HTTP checks use synthetic responses and never change real DNS records.

Native server behavior is based on the supplied server README and JavaScript API. MX Simulator and mxserver belong to their respective author. Made with Codex.
