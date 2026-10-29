# Previous installation cleanup

Before installation, Everything inspects the selected injection directory and
nearby previous-installer journals. It does not use a game-name allowlist.

- Recognized obsolete rendering addons are backed up before replacement.
- A legacy Swapper overlay is retired when its journal identifies this executable,
  records that exact addon as added, and its SHA-256 and addon identity match.
  A nearby parent journal is supported for games with nested engine folders.
- Unidentified addons, modified files and unverifiable ownership records remain
  in place. Preflight shows what needs review before you choose to continue.
- Locked files selected for legacy cleanup block installation before mutation.
- The install activity lists retired files; the final result reports a cleanup
  count. Restore uses verified snapshots to put those exact files back.

The earlier installer's backup directory is preserved. Its `added` list is not a
blanket deletion list: game DLLs, NVIDIA libraries, unrelated mods and user data
are not removed merely because an old journal mentions them.

File installation and cleanup do not certify that Phoenix processed a frame.
Use **Verify & fix** for runtime checks after installing.
