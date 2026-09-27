# Navisworks Element Tagger

Tags elements in the 3D view with their **Navisworks Quick Properties**, using
**native Navisworks redline markup** (not a custom overlay), and saves a
viewpoint for every tag.

Supports **Navisworks 2024, 2025, 2026 and 2027** (.NET Framework 4.8, x64)
from a single installer - see **Version support**.

---

## What it does

1. **ENABLE** in the panel makes the tagger the active Navisworks tool.
2. **Click 1** picks an element, selects it, and reads its Quick Properties.
3. **Click 2** places the text block. A leader arrow is drawn back to the element.
4. The text and arrow are written into a **saved viewpoint** named `Tag 01`,
   `Tag 02`, `Tag 03`, and so on.
5. Tag again **without moving the camera** and the new markup lands in the
   *same* viewpoint, which is renamed to the range it now covers — e.g.
   `Tag 01-03`. Move the camera and a new viewpoint is created.

Numbering continues from the highest `Tag NN` already in the document, so
re-opening a model and tagging again does not restart at 01 or collide with
existing viewpoints.

**The leader leaves the edge of the text facing the element** — bottom-right
corner when the element is down and to the right, left edge when the element is
to the left, and so on — so it never cuts back across its own text.

**The tag stays on screen after you place it.** Navisworks only draws redlines
while their viewpoint is the current one, so the add-in selects the new
viewpoint as soon as it writes it. The camera does not move, because the
viewpoint was captured from where you already were. Orbit or zoom away and the
markup behaves like any other Navisworks viewpoint.

Press **Esc** between the two clicks to cancel. **DISABLE** returns Navisworks
to the normal Select tool, and so does **closing the panel** - tagging never
keeps hold of the left mouse button once the panel it belongs to is gone.

**Every tag is read back after it is written.** Markup that is drawn on screen
is not proof that anything was stored, so the add-in asks the document what the
viewpoint now holds. If the answer is short, it writes the tag again; if that
fails too, the tag is given a viewpoint of its own and the panel says so. See
**Two markup backends** for why that matters.

---

## Install — on any machine

Run **`AB.Tagger-<version>.msi`** from the latest release (it replaces
`AB.Tagger.Setup.exe` and `NwTaggerSetup.exe`). It is a single Windows Installer
package, about 1.4 MB, with the plugin for every supported release inside it. It
runs no code of its own, so it installs on company PCs where Microsoft Defender's
attack surface reduction rules block unknown programs, and it needs no admin
rights for an Only me install.

Its pages:

- **Install for** — Only me (default: `%APPDATA%\Autodesk\ApplicationPlugins\NwTagger.bundle`)
  or Everyone (`%PROGRAMDATA%`, needs administrator rights).
- **Choose releases** — a tick per Navisworks release built, ticked where that
  Navisworks is installed; remembered for the next upgrade.
- **Earlier versions** — a copy installed by the retired `AB.Tagger.Setup.exe` is
  removed first unless you untick it. A copy installed for everyone can only be
  removed by an install for everyone, so the scope page suggests Everyone then.

It registers *AB Tagger for Navisworks* in Apps and Features.

**Close Navisworks first** — it holds the plugin file open while running. If it
is still open, Windows lists it and asks you to close it.

Silent options, for rolling out across a team:

```bash
msiexec /i AB.Tagger-1.2.0.msi /qn                 :: only me
msiexec /i AB.Tagger-1.2.0.msi /qn ALLUSERS=1      :: everyone (elevated prompt)
msiexec /i AB.Tagger-1.2.0.msi /qn ALLRELEASES=1   :: also releases not installed here
msiexec /i AB.Tagger-1.2.0.msi /qn NW2025=0        :: leave a release out
msiexec /x AB.Tagger-1.2.0.msi /qn                 :: uninstall, without a window
```

### Release notifications

Once a day at most, in the background, the tagger asks GitHub
(`Al-Qublawi/AB.Tagger.Navisworks`) whether a newer release exists, and says so
once per version. Nothing is sent but an anonymous request for the latest
release. Switch it off in **AB Adv Tools › About**, which covers every AB add-in.

### Building the installer

```bash
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

Compiles the plugin for each release and builds `dist\AB.Tagger-<version>.msi`
with the AB Adv Tools kit (`shared\ABAdvTools\msi`) from
`installer\Tagger.msi.psd1`, stamped with the plugin's version. The kit validates
the package and refuses to finish if it contains any step that runs code. Needs the
WiX CLI 5 (`dotnet tool install --global wix --version 5.0.2`, then
`wix extension add -g WixToolset.UI.wixext/5.0.2`). Add `-DryRun` to see what the
package would do on this machine without changing anything.

### Developing on it

`install.ps1` is the faster loop while working on the code — it builds and
copies straight into the per-user plugin folder without going through the
installer:

```bash
powershell -ExecutionPolicy Bypass -File .\install.ps1              # every release installed here
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Year 2027   # just one
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
```

It writes its own `PackageContents.xml` for exactly the releases it built, and
warns when an **all-users** copy of the bundle is installed as well — that one
declares the same plugin ids, so Navisworks loads one of them and ignores the
other, and you cannot tell which binary you are testing. Uninstall it (Apps and
Features → *AB Tagger for Navisworks*) before using a development install.

### Tests

Two sets, because the interesting half needs Navisworks and the other half does
not:

```bash
dotnet run --project Tests\NwTagger.Tests -c Release    # 18 checks, no Navisworks
```

covers the Excel and CSV export (rows, photos, row heights, XML validity), the
2027 redline JSON (round-trip, the captured sample byte for byte, escaping) and
the text layout.

**Tool Add-ins → "AB Tagger - Self test"** is the other half, and it needs a
model open. It tags a few elements by itself and then checks what the *document*
holds, leaving each viewpoint and coming back first so that markup still sitting
on the view cannot be mistaken for markup that was stored. It covers:

- several tags from one camera, all in one viewpoint, checked after every tag
- another tag after clicking away to a different viewpoint and back
- moving the camera starting a viewpoint of its own, without disturbing the first
- the export writing one row per viewpoint, with each photo embedded once
- closing the panel stopping tagging

It then removes the viewpoints it created and puts the camera, the view's markup,
the selection, the tag list, the tag numbering and the tool state back as they
were, and writes a report to the Desktop.

For an unattended run — testing a build without clicking through it:

```bash
set ABTAGGER_SELFTEST=1
set ABTAGGER_SELFTEST_QUIET=1
set ABTAGGER_SELFTEST_REPORT=C:\temp\tagger-selftest.txt
"C:\Program Files\Autodesk\Navisworks Manage 2027\Roamer.exe" C:\models\something.nwd
```

The test runs itself as soon as the model is open and writes that file. Without
`ABTAGGER_SELFTEST=1` none of this happens.

---

## On the AB Adv Tools ribbon tab

Since 1.1 the tagger sits on the **AB Adv Tools** tab, which every AB add-in
shares (Clash Approver and SwitchBack too), instead of a tab of its own:

| Panel | Buttons |
|---|---|
| AB Tagger | **AB Tagger** — shows or hides the dock panel |
| Tagging | **Enable** (toggles on while tagging is active), **Disable** |
| Tags | **Export to Excel**, **Clear tag list** |
| AB Adv Tools *(shared, always last)* | **About**, **Check for Updates**, **LinkedIn** |

Buttons enable and disable themselves with context — Export and Clear stay greyed
out until there is something in the tag list, and Enable greys out while tagging
is already running.

Navisworks gives every plugin its own tab, so each AB plugin declares the tab as
`ID_ABAdvTools` and the AB Adv Tools kit (`shared\ABAdvTools`, compiled into the
DLL) merges them at startup — see the kit's README for how and why that is safe.
`Plugins\TaggerStartup.cs` is the startup hook that does it.

The panel is also still available from **View → Windows → Element Tagger**, and
from the **Tool Add-ins** ribbon button.

The **AB Tagger** button carries the product logo and opens the panel; the panel
itself shows the logo in its header, and so does the installer.

### Where the ribbon files have to live

This is the one thing that silently breaks a Navisworks ribbon tab. The layout
XAML must sit in a **locale subfolder** beside the assembly, not next to it:

```
Contents\2026\
    NwTagger.dll
    en-US\
        NwTagger.xaml      <- the ribbon layout
    Images\
        *.ico              <- button icons
```

Autodesk's own `CustomRibbon` sample does exactly this in its post-build step
(`copy CustomRibbon.xaml -> Plugins\<name>\en-US\`). Put the XAML next to the
DLL instead and Navisworks reports no error at all — the tab simply never
appears. That is also why the XAML in the sample refers to images as
`..\Images\...`: it is resolving from inside `en-US\`.

Icons come from the `Icon` / `LargeIcon` names on each `[Command]` attribute,
which Navisworks resolves against the `Images` folder beside the DLL. That is
more predictable than the XAML-relative `Image=` paths, so the layout file does
not set them.

All of this is why the installer carries the whole build output for each release
rather than a single assembly.

---

## Version support

The installer carries a build for every release from **2024 to 2027** and lets
you pick which detected releases to install for.

| Release | API | Status | Markup backend |
|---|---|---|---|
| Navisworks 2024 | 21.0.0.0 | **Supported** | Object |
| Navisworks 2025 | 22.0.0.0 | **Supported** | Object |
| Navisworks 2026 | 23.0.0.0 | **Supported** | Object |
| Navisworks 2027 | 24.0.0.0 | **Supported** | JSON |

A plugin must be compiled against the API of the release it runs in. Navisworks
binds assemblies by strong name, and `Roamer.exe.config` only redirects within a
major version (`23.0.0.0 - 23.0.9999.9999`), so the 2026 build cannot load in
2024 (21.0.0.0) or 2025 (22.0.0.0). Shipping one DLL for all of them would
produce an add-in that fails to load rather than one that works.

### Two markup backends

**2027 removed the API the add-in originally used.** In 2025 and 2026,
`LcOpRedlineList` is publicly exported and `SavedViewpoint` exposes
`Redlines` / `EditRedlines()`. In 2027 (`Autodesk.Navisworks.Api` 24.0.0.0) the
list type is internal and both members are gone. The redline objects themselves
(`LcOpRedlineText`, `LcOpRedlineArrow`) are still public — you can create them,
but there is nowhere to put them.

What survives in 2027 is a JSON string on the view. So there are two backends,
sharing everything upstream — picking, text layout, leader geometry, export:

| | 2025 / 2026 | 2027 |
|---|---|---|
| Source file | `MarkupBackend.Objects.cs` | `MarkupBackend.Json.cs` |
| Store | `SavedViewpoint.EditRedlines().Add(...)` | `View.SetRedlines(json)` |
| Persist | (travels inside the viewpoint) | `ReplaceFromCurrentView(viewpoint)` |

Exactly one compiles, selected by `-p:RedlineBackend=Json`. Both expose the same
four members — `MarkupTravelsInViewpoint`, `Fill`, `Store`, `Count` — so
`ViewpointService` does not know which is in play, and `Count` is what lets it
read a write back instead of trusting it.

### Two rules that keep the markup, learned the hard way

Both of these lost **every tag in a viewpoint at once**, leaving the viewpoint
in the list with nothing in it:

1. **A viewpoint object is used once.** In 2024–2026 the markup rides inside the
   `SavedViewpoint`, and the obvious thing to do is to keep that object, add the
   next tag's redlines to it, and push it down again. Do that and the redline
   list handed back by `EditRedlines()` no longer belongs to the object the
   document has: `Clear` and `Add` still appear to work, the writes go nowhere,
   and the next copy pushed to the document carries no markup at all.
   `ViewpointService` therefore keeps the *camera* and the list of markup
   between tags, and builds a brand new `SavedViewpoint` for every single write.

2. **In 2027, the markup goes on after the viewpoint is current.** The markup
   lives on the view there, and selecting a saved viewpoint re-applies *its*
   markup to the view. Writing the JSON first and then replacing the stored
   viewpoint — which is what 1.1.1 did — leaves a window where Navisworks
   re-applies the viewpoint, wipes the view, and the capture that follows stores
   an empty redline set over the real one. So the order is: make sure the
   viewpoint is in the document, make it current, write the markup, then
   `ReplaceFromCurrentView`. The stored viewpoint is never replaced by a
   camera-only copy; renaming it uses `EditDisplayName` instead.

And because neither of those is visible while you are looking at the viewpoint
that still has the markup on screen, every write is read back with `Count`,
re-done once if it is short, and sent to a viewpoint of its own if that fails —
with the reason on the panel's status line. The self test checks all of this
from a real document.

Crucially, `LcOpRedline.ScreenToCameraSpace` is **still public in all three
releases**, so pixel → camera-space conversion is identical everywhere and the
markup geometry never had to be re-derived for 2027.

### The 2027 wire format

Captured from a live 2027 session and implemented in `RedlineJson`:

```json
{"Type":"RedlineCollection","Version":1,"Values":[
  {"Type":"RedlineText","Version":1,"Color":[1,0,0],
   "Origin":[-0.16009181714802512,0.013983923787026247],"Text":"ABC123"},
  {"Type":"RedlineArrow","Version":1,"Thickness":1,"Color":[1,0,0],
   "Start":[-0.18227321349985995,0.016877149398135144],
   "End":[-0.29221578672199766,-0.03423650306478853]}
]}
```

Points are camera space. Colours are 0..1. `RedlineText` carries **no size** —
that is the global `interface.redline.font_size` option, exactly as in 2026.

`RedlineJson` was verified against that captured sample: parse → re-serialise
reproduces the Navisworks output **byte for byte**, including its `"R"` number
formatting. Text escaping round-trips too, which matters because tag text comes
from model properties that can contain quotes and backslashes.

### The 2027 probe

`Probe2027` is the diagnostic add-in that produced the format above, kept for
re-verifying it against future releases. It has two commands: one dumps the
redline format of every saved viewpoint, the other writes generated markup and
checks it persists into a saved viewpoint — which is how the 2027 backend was
validated before any product code was refactored around it.

Install it with:

```bash
powershell -ExecutionPolicy Bypass -File .\install-probe.ps1
```

Then, in Navisworks Manage 2027:

1. Open any model.
2. **Review → Text**, and type something distinctive such as `ABC123`.
3. **Review → Arrow**, and drag one arrow.
4. Save the viewpoint.
5. **Tool Add-ins → AB Tagger - Dump Redlines (2027 probe)**.

It writes `ABTagger-Redline-Dump-<timestamp>.txt` to the Desktop containing, for
the live view and every saved viewpoint: the raw `GetRedlines()` string, a
base64 copy (so control characters survive being emailed), a guess at the
encoding, the camera, and the viewport size — the last two matter because
redline coordinates are camera space, so decoding them needs the viewport
aspect.

It finishes with a round-trip test: `TrySetRedlines()` fed the exact string
`GetRedlines()` produced. If that is **rejected**, generating the format is not
viable and 2027 needs a different approach entirely — worth knowing before any
decoding work starts.

The probe only reads. It restores the current viewpoint and camera when it is
done, and writes nothing to the model.

`Probe2027` is deliberately **not** in `NwTagger.sln`, so building the product
does not require Navisworks 2027 to be installed.

Uninstall it with `install-probe.ps1 -Uninstall`.

---

## Configure Quick Properties first

The tag text comes from Navisworks' own Quick Properties, via the COM
`SmartTagText` call — the exact same text Navisworks shows in its quick-property
tooltip. That means **whatever you configure there is what gets tagged**:

> Options → Interface → Quick Properties → Definitions

Add one row per property you want on the tag, e.g. `Element` / `Source File`, or
`Item` / `Name`.

**On this machine Quick Properties are enabled but have no definitions
configured.** Until you add some, the tagger falls back to the `Item` category
(Type, Name, Source File), which produces tags like the reference screenshots:

```
Item Name: Floor
Source File: OMAR SAMANIEGO ESTRUC-CALIDAD.rvt
```

To get output like `Componente Nombre: LADRILLO PASTELERO 24x24`, add that
category/property pair to the Definitions list.

---

## Panel reference

| Control | Effect |
|---|---|
| **Size** | Redline text point size |
| **Font** | Redline typeface |
| **Color** | Tag colour — swatch or `...` opens the colour picker |
| **Leader** | Arrow line thickness, 1–10 px |
| **Write data on click** | Off = clicking only selects the element, no markup is written |
| **Add category title** | Show `Category: value` instead of bare values |
| **Add "Item Name" header line** | Prefix the tag with `Item Name: {DisplayName}` |
| **Divide text in multiple lines** | Word-wrap long properties |
| **Characters per line** | Wrap width, 8–200 |
| **ENABLE / DISABLE** | Activate or deactivate the tagging tool |
| **Hide/Show tags in current view** | Toggles redlines in the live view only |
| **Export tags to Excel...** | Writes a real `.xlsx` (or `.csv`) — see below |
| **Clear tag list** | Empties the panel list; saved viewpoints are untouched |

---

## The Excel export

Four columns, **one row per viewpoint**:

| Column | Contents |
|---|---|
| **Tag Name** | The viewpoint, which already reads as a range: `Tag 01`, `Tag 04-06` |
| **File Name** | Every source model the tags in that viewpoint came from, once each |
| **Saved Viewpoint** | Photo of the viewpoint, with all of its tag markup drawn on it |
| **Comments** | Left blank for you to fill in |

Rows are sized to the photo and the comments column wraps, so it is ready to
type into as soon as it opens.

Up to 1.1.1 this was one row per *tag*, which meant a viewpoint holding four
tags repeated the same photo on four rows. Tags that share a viewpoint now share
its row: `Tag 04-06` is one line, one picture, one comment box. The `.csv`
option groups identically, so the two always list the same rows.

**How the photo is made.** Exporting steps the camera through each tagged
viewpoint and captures it with `ImageGenerationStyle.Scene` — geometry only,
without Navisworks' overlay — then redraws the leader and text on top with GDI+
from the geometry stored on each tag. Doing the markup ourselves guarantees the
photo shows exactly one copy of the tag rather than depending on the viewer's
overlay settings, and it renders correctly at export resolution rather than the
resolution the tag was placed at. Your current view is restored when it
finishes.

Several tags placed without moving the camera share one viewpoint, so they share
one photo — with **all** of their markup drawn on it — and now one row.

If a viewpoint cannot be photographed the row still exports, with `(no photo)`
in the photo cell. The `.csv` option writes Tag Name, File Name and Comments
only, since CSV cannot carry images.

---

## Known constraints

These are Navisworks limitations, not bugs in the add-in — worth knowing before
you file one against it:

- **Font name and size are global.** `LcOpRedlineText` has no per-object font.
  The panel writes `interface.redline.font_size` / `font_name` into GlobalOptions
  and reloads the kernel, so changing Size re-renders **every** redline in the
  document, not just new tags. Colour and line thickness *are* per-tag
  (`SetLineColor` / `SetLineThickness`), so those vary freely.
- **Leaving Size at 14 stores nothing.** 14 is Navisworks' own default, and
  Navisworks records a default-valued option as the single character `0`,
  meaning "not set". Reading that back is not a rejection - the tags are drawn
  at 14 either way - so the add-in stays quiet about it. Any other size is
  stored as itself (`2 26`) and is reported if Navisworks refuses it.
- **Settings are not persisted between sessions.** They live in
  `TaggerSettings.Current` for the life of the Navisworks process. The redline
  font/size/colour *are* persisted, because those are real Navisworks options.
- **Hide tags** works on the live view via `View.GetRedlines()` /
  `TrySetRedlines()`. It needs an empty-redline baseline, captured when the panel
  first opens on a document with no tags yet. If you open the panel after
  tagging, the toggle reports that it cannot hide rather than guessing.
- **While the tool is enabled it owns left-click.** Middle and right button are
  passed through so orbit/pan/zoom still work. Closing the panel turns tagging
  off, so the left button always comes back with it.
- **Tags are numbered from the viewpoints in the document**, so a self test run,
  or viewpoints you delete by hand, do not make the next tag collide with an
  existing name.
- **In 2027, a tag replaces whatever markup is on the live view.** Markup there
  is what the add-in writes and captures, and it cannot tell your redlines from
  its own - so redlines drawn by hand into the live view, and not saved into a
  viewpoint of their own, are gone once you tag from that camera. 2024 - 2026
  write into the tag's own viewpoint and never touch the view.

---

## Code map

```
NwTagger/                  the add-in itself
  Core/
    TaggerSettings.cs      Shared settings object + change notification
    GlobalOptionsBridge.cs Reads/writes Navisworks GlobalOptions via HKCU
                           + LcOpRegistry.LoadGlobalOptions()
    QuickPropertyReader.cs SmartTagText primary source, Item-category fallback
    ElementIdResolver.cs   Revit "Element ID" property, else model-tree index path
    TextLayout.cs          Word wrap, smart offset, edge collision
    RedlineBuilder.cs      Screen pixels -> camera space markup
    MarkupModel.cs         Version-neutral markup (text / arrow, camera space)
    MarkupBackend.*.cs     Two storage backends; one compiles per release
    RedlineJson.cs         The 2027 wire format, verified byte-for-byte
    ViewpointService.cs    Creates/reuses the saved viewpoint, Tag NN numbering,
                           reads every write back and repairs it
    TagService.cs          Orchestrates one tag end to end
    TagRecord.cs           Session tag list + markup geometry for the export
    ViewpointPhotoService.cs  Captures each viewpoint and redraws the markup
    TagExporter.cs         Dependency-free .xlsx (with images) and .csv writer,
                           one row per viewpoint
    TaggerSelfTest.cs      The in-Navisworks diagnostic: tags, reads storage back,
                           puts everything back
    ToolController.cs      Enable/disable, pane show/hide, and the watchdog that
                           stops tagging when the panel closes
  Plugins/
    TaggerToolPlugin.cs    ToolPlugin: two-click flow + OverlayRender preview
    TaggerPanePlugin.cs    DockPanePlugin host
    TaggerCommandPlugin.cs Tool Add-ins button
    TaggerRibbonPlugin.cs  The panels on the AB Adv Tools tab + shared suite commands
    TaggerSelfTestPlugin.cs  Tool Add-ins > "AB Tagger - Self test"
    TaggerStartup.cs       Joins the AB Adv Tools suite at startup (tab merge, update
                           check), and starts the self test when asked to by
                           ABTAGGER_SELFTEST=1
  UI/
    TaggerPaneControl.cs   The WinForms panel

  Images/                  logo.png plus generated .ico button icons
  NwTagger.xaml            Ribbon layout (deployed to en-US\)

installer/
  Tagger.msi.psd1          What the .msi installs where; everything else is the kit's
  product.ico              Apps and Features icon

shared/ABAdvTools/         the AB Adv Tools kit - shared tab, About, release checks,
                           .msi builder (vendored; edit the canonical kit and sync)

Tests/NwTagger.Tests/      The checks that do not need Navisworks (export, JSON, layout)

Probe2027/                 2027 redline-format probe (diagnostic, not shipped)
  RedlineDumpPlugin.cs     Dumps GetRedlines() per viewpoint + round-trip test

tools/
  make-icons.ps1           Regenerates Images\*.ico, including from logo.png
```

The icons are generated rather than hand-drawn. `make-icons.ps1` writes 32-bit
ICO files by hand, because `Icon.FromHandle(...).Save()` drops the alpha channel
and leaves black fringing on the ribbon.

### The APIs this is built on

Redline markup is not in the documented Navisworks .NET surface, but the types
are publicly exported from `Autodesk.Navisworks.Api.Interop`:

| Purpose | API |
|---|---|
| Text markup | `new LcOpRedlineText(string, Point2D)` |
| Leader arrow | `new LcOpRedlineArrow(Point2D, Point2D)` |
| Attach to viewpoint (2025/26) | `SavedViewpoint.EditRedlines().Add(...)` |
| Attach to viewpoint (2027) | `View.SetRedlines(json)` + `ReplaceFromCurrentView` |
| Pixels → redline space | `LcOpRedline.ScreenToCameraSpace(view.Viewer, x, y)` |
| Element picking | `View.PickItemFromPoint(x, y)` |
| Quick property text | `ComApiBridge.State.SmartTagText(path)` |
| Reload options | `LcOpRegistry.LoadGlobalOptions()` |
| Viewpoint photo | `View.GenerateImage(ImageGenerationStyle.Scene, w, h, true)` |
| Keep markup on screen | `SavedViewpoints.CurrentSavedViewpoint = item` |

`Graphics` window context shares the mouse coordinate system — pixels, origin at
the **top left**. Flipping Y there makes the placement preview track the cursor
inverted.

---

## Adding another release

2024 and 2025 are missing only because their API assemblies are not on the build
machine. To add one, you need three files out of that release's install folder:

```
Autodesk.Navisworks.Api.dll
Autodesk.Navisworks.ComApi.dll
Autodesk.Navisworks.Interop.ComApi.dll
```

Drop them in `refs\<year>\` — for example `refs\2024\` — and rebuild:

```bash
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

That is the whole process. The build script compiles one plugin per release it
can find an API for, and the .msi gets a tick for each release it is given — so
no code changes anywhere. A release installed locally is found automatically; `refs\<year>\`
exists for releases that are not.

Series codes are the release year minus 2003, so 2024 is `Nw21` and 2027 is
`Nw24`. Every release in this range is .NET Framework 4.8, so there is no
retargeting involved.

---

## Author

[Abdullah Lotfy](https://www.linkedin.com/in/abdullahalqublawi/) — the panel,
the ribbon tab and the installer all link here.
