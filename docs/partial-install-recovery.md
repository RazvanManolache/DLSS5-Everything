# Blocked Files and Partial Installation

Installation attempts every file in the selected route. An ordinary copy or
configuration-file I/O failure does not stop the remaining files. This applies
to ReShade, Phoenix add-ons, runtime helpers, shaders, models, extra add-ons,
configuration files and launchers, without a special exemption for any filename.

The final warning lists the files that could not be installed and their errors.
A file may be blocked by antivirus, locked by another process, read-only, or
inaccessible because of permissions. The installer cannot determine which of
these is responsible from a generic access-denied error. It never disables
antivirus, restores quarantined files or adds exclusions.

## Retry or Remove

1. Review the warning and, when relevant, your antivirus detection history.
2. Close programs holding the affected files. Resolve permissions or obtain a
   reviewed replacement from the publisher; do not assume a detection is false.
3. Install again to retry the files. The installer preserves the original game
   backups while repairing a partial installation.
4. Use Restore to remove the installer-owned files and restore recorded originals.
   Files that could not be backed up are left unchanged and are not claimed as
   newly installed files.

The manifest retains the failure list and removal ownership. Partial installs
are not reported as ready by verification or the installed-game check. Temporary
launches and automatic verification do not launch an incomplete installation.
The command-line installer returns exit code `2` for a partial installation,
`1` for a fatal error, and `0` when every requested file operation completed.
Successful file deployment alone does not establish working inference.

## Safety Stops

Unsafe paths, links, a running game, invalid payloads, damaged original backups,
runtime checksum mismatches, cancellation and failure to persist the recovery
journal still stop installation. Continuing in those situations would risk
untracked changes or replacing game files without a trustworthy backup.

Explicit Restore and interrupted-transaction recovery remain strict: if a file
is still blocked, recovery stops and keeps its journal so it can be retried.
An incomplete but finished file-copy attempt is different from an interrupted
transaction; the former can be repaired with Install again.
