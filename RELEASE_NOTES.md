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
