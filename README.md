# right-hand-backup

Backs up the small files your background tools keep outside a repo: their state, their queues, and their records of
what they've already done. It takes a snapshot only when something has changed, keeps a sensible spread of them, and
puts them back with a restore that shows what it would change before it changes anything.

Those files are usually tiny, and losing one is rarely a disaster, but it is rarely harmless either: a lost queue is
work silently dropped, and a lost record of what was done is something done twice. **The bigger risk is restoring
stale state**, such as a day-old record of bookings put back over today's, so a restore goes to a staging folder
first, says file by file how the snapshot compares with what's live, and flags the records that stop something being
done twice.

```
right-hand-backup run
```

```
Snapshot 2026-11-19-2339: 540 files, 3.1 MB, in C:\Users\me\OneDrive\right-hand-backup\MYPC.
```

```
right-hand-backup restore
```

```
Snapshot 2026-11-19-2339 from MYPC (taken 19/11/2026 23:39), compared with live:
  older      my-tool/bookings.json     snapshot 19/11 23:35, live 19/11 23:41  [!]
  new        my-tool/handlers/1.json   snapshot 19/11 22:10, not there live
  same       my-tool/queue.json
3 files: 1 new, 1 same, 1 older.
[!] This file stops something being done twice (a booking, a forward), and the snapshot's copy is older than
    the live one: ...
Copied to C:\Users\me\AppData\Local\right-hand-backup\restore\MYPC-2026-11-19-2339; nothing live was changed.
```

## Install

Windows (x64) only; nothing else is needed.

1. Download `right-hand-backup-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/right-hand-backup/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\right-hand-backup`.
3. `right-hand-backup config` creates the settings file; name your folders and files (below).
4. `right-hand-backup run --dry-run` lists what a run would copy: **read it once**, to be sure nothing sensitive is
   in it. `right-hand-backup run` takes the first snapshot.
5. `right-hand-backup autostart on` keeps it going by itself: every 2 hours, at logon, and 5 minutes after each
   unlock (which catches up after sleep).

## How a run works

1. **It walks each source folder** for the files its `include` patterns name, less its `exclude` patterns.
2. **A quiet pass:** it copies every file (reading alongside the program that owns it, so nothing is locked), then
   hashes each one again. If any changed meanwhile, it does the whole pass again, up to three times, so **the
   snapshot is one moment**: a queue and the record that must match it are never from different times. After the
   third try it takes what it has and says which files were still changing (in its output, log and manifest). A file
   another program holds without sharing is skipped and listed; one skipped three runs in a row is notified.
3. **If nothing changed** since the last snapshot (the same files with the same SHA-256), nothing is written, so a
   synced folder isn't churned. Either way, `latest.json` records when it last checked.
4. **Otherwise it writes a snapshot**, `<destination>\<machine>\YYYY-MM-DD-HHmm\`, with a folder per source and a
   `manifest.json` (each file's path, size, SHA-256 and time; what was skipped, refused or still changing). It is
   written as `.partial` and renamed when complete, so a half-written one never looks real.
5. **It removes old snapshots** by the `[keep]` rules: everything from the last 48 hours, then the newest of each day
   for 30 days, then the newest of each week for 12 weeks. The newest is always kept. Only folders it wrote (named as
   snapshots, holding a manifest) are ever deleted.

**What's never copied, whatever the settings say:** Edge and other browser profiles (`*edge-profile*`, `User Data`),
cookies and saved passwords, sign-in token caches (`msal-cache*`, `*token-cache*`), credentials files
(`.credentials.json`, `credentials.json`), keys and certificates (`.ssh`, `.gnupg`, `*.pfx`, `*.p12`, `*.pem`,
`*.key`), contact cards (`*.vcf`) and org-directory's state (personal contact details). A careless include, `**` or
`*.json` in a folder holding a token cache, can't copy one: it's listed as blocked instead. **Signing in again is the
safer recovery.** The same list applies to a restore.

**Safety:** if the files to copy come to more than `max_snapshot_mb` (default 50), nothing is written and you're
told at once: something big got included. A run that fails is logged and notified on the third failure in a row (a
run on waking can find the synced folder not there yet), once, until a run succeeds.

**No extra encryption.** Snapshots are as protected as the folder they go to: choose one your organisation governs
(OneDrive for work encrypts at rest). A key would be one more thing to lose, and Windows' own (DPAPI) is tied to the
machine, which defeats a restore on a new one.

## Restoring

```
right-hand-backup restore [SNAPSHOT] [--from MACHINE] [--only PATTERN ...] [--to DIR]
right-hand-backup restore [SNAPSHOT] [--from MACHINE] [--only PATTERN ...] --apply
```

1. **`restore`** checks the snapshot against its manifest (it refuses one that doesn't match), copies it to a staging
   folder (`[restore] staging`, or `--to`), and prints each file: **new** (not there live), **same**, **older** (the
   snapshot's copy was changed before the live one: restoring goes back in time), **newer**, or **refused** (a path
   on the list above) or **no source** (a source your settings don't have). Nothing live changes. The comparison is
   saved beside the files as `comparison.txt`.
2. **`restore --apply`** writes the new and changed files over live state. **It refuses** until a staging restore of
   that snapshot has been done (so you've seen what it will change), and **while any task in `[restore] stop_tasks`
   is running** (a tool working on its state as it's replaced would undo the restore, or act on half of it). Each live
   file it replaces is kept first, in `replaced-<time>` in the staging folder.
3. **Files in `warn_if_older` are flagged `[!]`** when the snapshot's copy is older than the live one: records that
   stop something being done twice. Before starting their tool again, check what it has done since the snapshot.

`--only` restores just the files named ("source/path" patterns, repeatable): one damaged file, say.

**On a new machine:** install the tools and this one, restore **before** any task is registered (or the tools start
on empty state and redo things), `restore --from <old machine>`, read the comparison, `--apply`, then sign in again
and register the tasks. Sources are matched by name, so the new machine's settings decide where each one goes.

## Commands

```
right-hand-backup run [--dry-run]       take a snapshot if anything changed (--dry-run: list it, write nothing)
right-hand-backup list [--from MACHINE] the snapshots kept, and the other machines' folders
right-hand-backup verify [SNAPSHOT]     re-hash a snapshot against its manifest (default: the newest)
right-hand-backup restore [SNAPSHOT]    to a staging folder, compared with live (--apply to write it back)
right-hand-backup autostart on          run every few hours, at logon and after unlocking (off, restart, status)
right-hand-backup config                create the settings file (if missing) and say where it is
```

`--destination DIR` (before or after the command) uses another snapshots folder, to try it out without touching the
real one.

**Exit codes:** 0 done; 1 failed, refused, or a snapshot that doesn't match its manifest; 2 settings or arguments.

## Settings

`%APPDATA%\right-hand-backup\config.toml` by default (`--config` or `RIGHT_HAND_BACKUP_CONFIG` for another). Read
strictly: an unknown setting, or one of the wrong type, is an error that names it. Paths take `~` and environment
variables (`%LOCALAPPDATA%`); one that's not set is an error, not a folder named after it.

```toml
base = "base-config.toml"         # optional: settings shared with other tools; its [paths] are variables here
destination = "%OneDriveCommercial%/right-hand-backup"   # a folder per machine below it
machine = "%COMPUTERNAME%"        # optional: this PC's folder name (default the computer's name)
log_file = "..."                  # optional; default %LOCALAPPDATA%\right-hand-backup\right-hand-backup.log
state_file = "..."                # optional: only failures and files skipped in a row
every_minutes = 120               # autostart
after_unlock_minutes = 5          # autostart; 0 for none
max_snapshot_mb = 50
notify = true

[keep]
all_hours = 48
daily_days = 30
weekly_weeks = 12

[[source]]
name = "my-tool"                  # its folder in each snapshot; letters, digits, '.', '_', '-'
folder = "%LOCALAPPDATA%/my-tool"
include = ["*.json", "handlers/**"]
exclude = ["*.bak-*", "*.tmp"]

[restore]
staging = "%LOCALAPPDATA%/right-hand-backup/restore"
stop_tasks = ["my-tool run"]      # Task Scheduler names
warn_if_older = ["my-tool/bookings.json"]
```

**Patterns** are relative to the source folder, with `/` between folders: `*` and `?` stay within one folder, `**`
spans any number of them. So `*.json` is the folder's own JSON files, not its subfolders'; `handlers/**` is everything
below `handlers`; `**/*.json` is JSON anywhere. Case is ignored. A walk only goes into folders a pattern could reach,
so a large folder beside the files you want costs nothing. Links and junctions aren't followed.

**What to include:** what can't be recreated, or would be redone if lost: queues, records of what was sent, booked,
forwarded or raised, and anything a person decided by hand. **What to leave out:** caches a tool fetches again, logs,
temporary files, and anything with a sign-in in it (which the tool refuses anyway). The destination can't be inside a
source, or a source inside it.

## Building

.NET 11 (see `global.json`), with the shared libraries from
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) checked out beside this repo:

```
dotnet test -c Release
dotnet publish src/RightHandBackup.Console -c Release -r win-x64 -o out    # Native AOT
```

A tag `vX.Y.Z` builds a release (`.github/workflows/release.yml`), against the library commit pinned there.
