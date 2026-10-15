# Source publication review — 2026-10-15

The user will perform the GitHub push and intends a public repository. No remote
destination has been supplied or configured. No push, remote creation/change,
force-push, GitHub repository, or Release was performed.

## History and contents

The starting main was `ee7cfa4`: the preserved 11 sanitized commits through
`9d35041`, followed by the Save Queue commit. The hotkey work is on
`codex/fix-global-hotkeys`; it adds a normal commit without amending, rebasing,
squashing, or replacing any existing commit. Only an explicit main ref should
eventually be pushed. Local `refs/codex/*`, other branches, reflogs and ignored
snapshots are outside the publication scope; do not use --mirror or --all.

The read-only audit checks every blob reachable from the selected ref, including
font signatures/metadata strings rather than only filenames. In starting main's
12 commits/309 objects it found exactly the four approved current Inter/Rewindtica
font blobs, no Coolvetica binary, no generated/runtime/binary output paths, and
no matches for the credential patterns checked. A pattern scan is not a guarantee
that no secret exists. Repeat on the exact final commit before pushing:

```powershell
python scripts/audit-source-publication.py main artifacts/publication-audit.json
git status --short
git branch -vv
git remote -v
git for-each-ref --format="%(refname) %(objectname)"
```

The output report is ignored. `.tools`, artifacts, bin/obj, runtime settings,
queues, recordings, credentials and generated test media remain excluded. No
FFmpeg/.NET executables or DLLs are tracked or proposed for source publication.

## Notices and unresolved permissions

GPL-3.0-or-later LICENSE/NOTICE and adapted MP4 COMPRESSOR attribution are present.
The two GPL text copies match; the documented upstream LF hash differs from a
Windows CRLF working-copy hash. Inter and Rewindtica retain complete SIL OFL 1.1
texts, upstream attribution and derivative notice; all are copied by publish.
See [THIRD-PARTY.md](../THIRD-PARTY.md).

**Public source publication remains blocked by graphics provenance.** Obtain and
record author/source/license or explicit source-redistribution permission for:

- Every `assets/figma/*.svg`, including icons whose geometry is embedded in
  `src/Replay/App.xaml` and `OverlayVectors.xaml`.
- The artwork behind `assets/app/Rewind.png` / `Rewind.ico` and
  `assets/tray/RewindTray.png` / `RewindTray.ico`.

Permission must cover the included assets and their copies in the preserved
publication history, and any required attribution must accompany the source.
Figma access/design approval alone is not that evidence. Missing documentation
does not establish infringement; it means this review cannot approve public
redistribution yet. Do not remove/rewrite the approved history to work around
this without a separate user decision.

Binary redistribution is a separate blocker: arrange the exact FFmpeg build's
GPLv3 Corresponding Source distribution mechanism and bundled-library notices
before uploading packages. Runtime/font/root notices in the local manual build
do not resolve that FFmpeg obligation. The local manual build is for testing;
do not upload it as a release asset. See [GPLv3 sections 4–6](https://www.gnu.org/licenses/gpl-3.0.html)
and the [OFL redistribution guidance](https://openfontlicense.org/how-to-modify-ofl-fonts/).

## Final publication step (user-owned)

After permissions are resolved, verify the exact existing/new GitHub destination
and public visibility, inspect the remote repository's existing refs, and compare
them with the audited local main. Add a remote only for that verified destination,
without overwriting any existing remote. Push only `main:main`, with no force
option. Never infer a repository URL from a username or project name. No GitHub
Release is prepared or authorized by the local build.
