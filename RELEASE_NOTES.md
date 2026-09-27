# AB Tagger 1.2.0

Three bugs reported from live use, a fourth found while testing the fixes, and the tests
that would have caught all of them.

## Tags no longer disappear from a viewpoint

Placing a third or fourth tag without moving the camera could empty the whole
viewpoint: the markup went off the screen and out of the document at once,
leaving `Tag 01-04` in the Saved Viewpoints list with nothing in it. It happened
in every release, for two different reasons, and both are fixed:

- **2024, 2025, 2026** kept one viewpoint object and added each new tag's
  redlines to it. After the document has copied that object once, the redline
  list it hands back no longer belongs to it - the writes go nowhere, and the
  next copy pushed to the document carries no markup at all. A brand new
  viewpoint object is now built for every single write.
- **2027** stores markup on the view and captures it into the viewpoint.
  Selecting a saved viewpoint re-applies *its* markup to the view, so writing
  the markup before the stored viewpoint was replaced left a window where
  Navisworks wiped the view and the capture then stored an empty set over the
  real one. The order is now: viewpoint in the document, viewpoint current,
  markup written, then captured - and the stored viewpoint is never replaced by
  a camera-only copy.

On top of the fixes, **every tag is now read back out of the document**. If the
markup is not there, the tag is written again; if that fails, it is given a
viewpoint of its own and the panel says why, instead of a tag quietly going
missing.

## The Excel export lists each viewpoint once

Tags that share a viewpoint - anything tagged without moving the camera - used to
get a row each, which repeated the same photo three or four times over. They now
share one row: `Tag 04-06`, every source file in that viewpoint listed once, one
photo, one comment box. The `.csv` option groups the same way.

## Closing the panel stops tagging

Pressing the panel's **X** left the tagger still owning every left-click in the
3D view, with no panel to turn it off from. Navisworks tells a plugin nothing
when its dock pane is closed, so the add-in now watches for it: closing the
panel does exactly what **DISABLE** does.

## No more "Navisworks did not accept the text size"

Pressing **Enable** on the ribbon in 2026 and 2027 put up a dialog saying
Navisworks had rejected the text size, "it reports 0". It had not. `0` is how
Navisworks records an option left at its built-in default, and 14 - the size the
panel starts at - is that default, so anyone who never touched Size got the
dialog every single time they started tagging. The add-in now reads `0` for what
it is. A size Navisworks genuinely refuses is still reported.

## Diagnostics and tests

- **Tool Add-ins > "AB Tagger - Self test"** tags a few elements by itself and
  checks that the markup is really stored, leaving each viewpoint and coming back
  first, then puts the document, the camera, the tag list and the tool back as it
  found them and writes a report to the Desktop. It is how the fixes above were
  verified in a real model.
- `Tests\NwTagger.Tests` covers the export, the 2027 redline JSON and the text
  layout with Navisworks closed.
- `install.ps1 -Year 2027` builds and installs one release for development, and
  warns when an all-users copy would shadow it.

Nothing else changed: the same panel, the same ribbon, the same installer pages
and switches as 1.1.1.

---

# AB Tagger 1.1.1

Element tags from Quick Properties, as native Navisworks redline markup, with a saved viewpoint per
tag and an Excel export. Navisworks Manage and Simulate 2024 – 2027.

## The installer is an .msi: `AB.Tagger-1.1.1.msi`

1.1.0's `AB.Tagger.Setup.exe` was blocked on company PCs by Microsoft Defender's attack surface
reduction rule *"Block executable files from running unless they meet a prevalence, age, or trusted
list criteria"*. That rule stops unknown programs, not Windows Installer packages, and the new
package runs no program of its own. The tagger itself is unchanged.

Everything 1.1.0's installer offered is still there:

- **Only me** (the default, no administrator rights) or **Everyone**
- the Navisworks releases installed here, ticked — now one tick per release, remembered for the
  next upgrade
- **earlier versions**: a copy installed by 1.1.0's `Setup.exe` is removed first unless you untick it
- Apps and Features for uninstall and repair
- silent deployment: `msiexec /i AB.Tagger-1.1.1.msi /qn`, and `msiexec /x … /qn` removes without a
  window, as `/uninstall` did

If Setup.exe 1.1.0 is installed, just run the .msi: it replaces it. Close Navisworks first.

---

# AB Tagger 1.1.0

## What's new

### One ribbon tab for every AB add-in

The tagger's panels now sit on the **AB Adv Tools** tab, shared with AB Clash Approver and AB
SwitchBack, instead of a separate *AB Tagger* tab. At the end of the tab is one **AB Adv Tools**
panel for the whole suite:

- **About** lists every AB tool loaded, with its version.
- **Check for Updates** checks them all.
- **LinkedIn**.

That panel replaces the LinkedIn button that used to be in the Tags panel. Tagging, the panel and
the export are unchanged.

### Release notifications

Once a day at most, in the background, the tagger checks for a newer release and tells you **once
per version**. The only thing sent is an anonymous request for the latest release. Turn it off in
**AB Adv Tools › About**.

### A new installer: `AB.Tagger.Setup.exe`

It replaces `NwTaggerSetup.exe` and:

- **finds earlier copies** of the tagger, for this user or for all users, and removes them first
  unless you untick them
- registers in Apps and Features for a normal uninstall
- keeps the command line: `/silent`, `/silent /allusers`, and `/uninstall` still removes without a
  window (current user, or `/allusers`); adds `/scan`

Close Navisworks before installing.
