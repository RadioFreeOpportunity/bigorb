# Changelog

## 1.2.0

Big Walk 1.6.0 support. 1.1.1 and older will not work on 1.6.0: the log fills with
TypeLoadException, sign locks stop holding, and bans can stop matching. Update before
hosting.

Identifiers

- 1.6.0 changed what the game calls a player's identifier. It used to be the Steam ID (or
  PSN/Xbox id); it is now the Epic account id, and the platform id moved to a separate
  field. Big Orb reads the platform id from that field, so bans, the chat and sign logs,
  nametags and the dashboard are keyed the same way they always were. Your bans.json
  and any CSVs you shared or imported keep working unchanged.
- The handshake check (idspoof, ban gate) reads the platform id from the new handshake
  field. The old field is gone.
- idrotate no longer counts a player's own Epic id as a second identifier.
- platspoof compared the reported identifier with the platform id. Those are now
  different kinds of id by design, so the check no longer fires. epicspoof still does.

Signs

- The game no longer lets the client say who wrote a sign; the server works it out from
  the connection. Big Orb's lock follows suit, so a guest can't get past a lock by
  sending the host's identifier anymore (the 1.1.x lock trusted that value).
- Setting and locking signs from the dashboard uses the game's new server-side text
  setter, the same one the game uses for its own sign writes.
- Sign log entries show the platform id, not the Epic id, for the author.

Kicks

- 1.6.0's own kick erases the signs the kicked player wrote that session. Big Orb's kick
  and ban (including autobans) now do the same.

Join codes

- 1.6.0 join codes are letters and numbers instead of digits only. The dashboard's
  Session Join Code pill shows and copies the new codes exactly as the game makes them.
- The pill was blank on 1.6.0 because the game renamed its lobby manager. It reads from
  the new one now.

## 1.1.1

Hotfix for Proton/Linux users

- The dashboard failed to start under Wine/Proton ("Call not implemented"). Wine's http
  layer accepts only one URL per listener, and the server registered two (localhost and
  127.0.0.1). It now falls back to a single prefix when the pair is refused. Windows is
  unaffected.

## 1.1.0

Defence against modded clients

- Bans now record the connection's network identity (EOS ProductUserId) next to the
  account identifier and are enforced at the authentication handshake, before the player
  spawns. Bans made with 1.0.0 pick up the address the next time that identifier is seen.
- Kicks are enforced server-side. The game's own kick is a message the client is asked to
  act on; a modified client can ignore it.
- Every new connection is looked up on Epic. A Steam-linked account claiming a different
  Steam ID is a proven spoof. An account with no linked Steam/PSN/Xbox login at all is an
  anonymous device login, which the unmodified game never uses.
- The identity fields a client reports (epic id, platform id) are checked against the
  connection once they arrive. Mismatches are proof of a modified client.
- One address claiming several identifiers is flagged; three or more is banned.
- Voice packets are rate-limited per connection at the relay (default 120/s; a talking
  player sends about 50). Sustained flooding bans the connection.
- Guest chat that imitates a system message ("host has been removed") is dropped.
- New alert kinds: idspoof, idrotate, epicspoof, platspoof, eosspoof, eosanon, voiceflood,
  fakesys. New config section `guard` with `autoBan`, `banAnonymousLogins`,
  `voicePacketsPerSecond`, `dropFakeSystemChat`.
- The ban list ships with the connection address of a client seen impersonating players
  and flooding voice in several hosts' lobbies.

Ban list sharing

- Export the ban list as CSV (`/api/bans.csv` or the button) and import one from another
  host. Imports merge and never remove.
- Manual ban field accepts a connection address as well as an identifier.

World tools

- Puzzle reset: every puzzle back to its loaded state (pieces, vice, gourd). Hub reset.
  Black tower finale reset by stage or whole. Live progress view: gourds turned in, puzzles
  launched, hubs and key state, gourds loose in the world with a pull-to-me button.
- Solve feed with an optional chime.
- Item reset: items back to where they were when the world loaded, near you or everywhere,
  or by prop group. A fresh-world baseline for the 4-player world is built in; other sizes
  are captured the first time they are hosted.

Other

- Player entries in the API carry the connection address.
- `eosmap.tsv` records which real account each connection address resolved to.

## 1.0.0

Initial release.
