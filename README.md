# HLA FOM Reader

A Windows desktop app for people who work with **HLA FOMs** — the files that spell out what data a
group of simulators is allowed to exchange.

It does two things:

- **Keeps a library of your FOMs.** Point it at a file and it reads the whole model into a small
  database on your PC. After that you can browse every class, attribute and datatype without opening
  the original file again.
- **Compares any two of them.** Item by item, with a plain answer to the question that usually
  matters: *if data has to move from this FOM to that one, what actually has to change?*

It reads both generations of HLA — the older **HLA 1.3** files (`.fed` and `.omt`) and the newer
**IEEE 1516** XML ones (1516-2000, 1516-2010 "HLA Evolved" and 1516-2025) — and puts them side by
side as if they had been written the same way.

---

## New to HLA? Start here

The app speaks the vocabulary of the standard. This is what those words mean:

| Word | What it means here |
| --- | --- |
| **Federation** | A set of simulators running together as one exercise. |
| **Federate** | One of those simulators. |
| **RTI** | The middleware in the middle that passes data between federates. It loads the FOM and enforces it. |
| **FOM** | Federation Object Model — the agreed list of everything the federation can exchange, and what each piece of data looks like. |
| **Object class** | A kind of thing that persists and gets updated: `Aircraft`, `Customer`. Classes inherit, so `Aircraft` gets everything `Platform` has. |
| **Attribute** | One field on an object class: `Position`, `Speed`, `PartySize`. |
| **Interaction / parameter** | A one-off event message rather than a lasting object, and its fields. |
| **Datatype** | What a value looks like as raw bytes — how many, signed or not, how a structure is laid out. Two attributes only interoperate if their bytes agree. |
| **OMT** | Object Model Template — the standard shape every FOM document follows. Its file format is the "OMT DIF". |
| **MIM** | The built-in classes and datatypes (`HLAobjectRoot`, `HLAinteger32BE`, …) the RTI supplies. A 1516 FOM uses them without declaring them. |
| **MOM** | The RTI's own management classes, for monitoring the federation itself. |

Two rules of thumb explain most of what this app does:

1. **Names are cheap; bytes are not.** Renaming a datatype changes nothing on the wire. Changing how
   many bytes it takes changes everything.
2. **What a class inherits counts.** A class can declare nothing at all and still carry 45
   attributes.

---

## Getting started

1. Run `HLAFomReader.exe`. Nothing to install, and no .NET needed.
2. The first launch asks where to keep your library. Take the default (`hlafomreader.db`, beside the
   exe) unless you have a reason not to. You can put a password on it.
3. Click **Register FOM…** and add your files.
4. Double-click anything in the list to explore it.
5. Switch to **Compare**, pick two FOMs at the top, and press **Compare**.

The sidebar on the left switches between the three screens. On a laptop it eats width the tables
could use, so the **«** button in its top-right corner — or **Ctrl+B** — shrinks it to a strip of
icons and gives those 144 pixels back to the data. Every row keeps its icon, so the strip is the
same list seen narrower rather than somewhere new; hover one to see what it is. It stays however you
leave it, next launch included.

**Light or dark** is a pair of labelled buttons on the **Settings** screen — the one place it is
set. The window repaints as you choose it; there is nothing to restart. A machine that has never
chosen follows the Windows app theme, and once you pick a side here that is what you get from then
on.

---

## The screens

### Registry — your library

**Register FOM…** asks which standard your model follows *first*, because the answer decides how
many files it needs:

- **HLA Evolved / IEEE 1516** — one `.xml`. Select several at once if you like.
- **HLA 1.3** — **two** files, and this is the part that catches people out. The `.fed` is what the
  RTI loads: it has the class structure but **no datatypes whatsoever**. The `.omt` (or `.omd`) is
  the documentation the RTI never reads, and it is the only place the datatypes, units, sharing and
  descriptions live. Pick the FED and the matching OMT is filled in for you if it sits in the same
  folder. The two become **one** entry: structure from the FED, meaning from the OMT, and wherever
  they disagree the FED wins, because the FED is what actually runs.

You can register a FED on its own. The app allows it and warns you: that entry will have no
datatypes, which limits what a comparison can tell you.

Where the two files have drifted apart, the app says so rather than quietly picking one. On a real
vendor pair it found nine interaction classes the OMT documents but the FED does not, and four MOM
classes the FED has that the OMT never mentions.

Everything the reader understood goes into the database — not as one lump, but as proper rows, one
table per part of the model. The panel on the right then shows:

- **Details** — the model's header information, how many of each thing it holds, file size, a
  SHA-256 fingerprint, and when it was last read.
- **Structure** — a tree of exactly what was stored: object classes and their attributes,
  interactions and their parameters, all six datatype tables, dimensions, routing spaces, switches,
  tags, time representation and notes.
- **Diagnostics** — every warning and error the reader raised, with line numbers.

**Double-click any FOM** for the full-screen explorer: the tree on the left, and on the right the
selected item's own properties plus a table of everything it contains — attributes, parameters,
record fields or enumerators — with every OMT column (DataType, Cardinality, Units, Resolution,
Accuracy, UpdateType, Ownership, Sharing, Transportation, Order, Dimensions, Routing space,
Semantics). Search filters the tree by the name of an item *or* of anything inside it.

**Export to Excel…** writes the FOM's two class trees to an `.xlsx` workbook with a tab each —
*Object Class Hierarchy* and *Interaction Class Hierarchy*. Each sheet lays the tree out as a
staircase: a class's name sits in the column matching its depth, so `ObjectRoot` is in **Level 1**,
`BaseEntity` in **Level 2** and `PhysicalEntity` in **Level 3**. Read down one column for every
class at that depth; read across a row for one class's ancestry. Beside the staircase sit the
qualified name, sharing, the depth as a number, and how many attributes (or parameters) the class
declares itself versus inherits — the same split the screen shows. The writer is hand-rolled
against the SpreadsheetML schema, so the app takes no Office dependency.

An entry is marked **stale** when the file on disk no longer matches the fingerprint taken when you
registered it, and **missing** when the file has gone altogether. Either way you can still compare
it, because the app works from its own copy rather than re-reading the file.

### Compare — what changed

Pick two registered FOMs once at the top. All three tabs below work from that pair.

#### Attribute data — the tab to start with

This is the remapping view, and the one to use when the question is *what data moves*. One row per
attribute a class really has — inherited ones included — with its datatype on each side:

| Status | What it means for you |
| --- | --- |
| Same | Same attribute, same bytes. Nothing to do. |
| Renamed | The datatype has a different **name** but the **same bytes**. Nothing to convert. |
| **Changed** | The bytes genuinely differ. **This is the work.** |
| Moved | Same attribute, same bytes, declared somewhere else in the family tree. Inheritance means it is still there. |
| Only in A / Only in B | Data with nowhere to go, or nothing to fill it. |

**Export CSV…** writes the visible rows as a remap worksheet:
`Class,Attribute,Status,DeclaredInA,DataTypeA,DeclaredInB,DataTypeB,Note`.

<details>
<summary><b>Why it compares bytes instead of names</b></summary>

Datatype names are looked up in each FOM's own datatype tables and boiled down to what they actually
are — `uint:32`, `array(char:8,n)`, `record(float:64,float:64,float:64)` — and *those* are what get
compared.

Comparing names alone is misleading across a generational move, which renames nearly everything:
`octet`→`Octet`, `unsigned long`→`UnsignedInteger32`, `float`→`AngleRadianFloat32` are all the same
bits. Units, resolution, accuracy and field *names* are left out of that boiled-down form because
they do not change the wire. Field **order**, array **length** and structure do, so they stay in.
Signed versus unsigned stays in too — it changes how the same bytes are read.

An **enumeration is treated as whatever it is stored in**, with no wrapper around it, for the same
reason units are dropped: the list of allowed values says which values are *legal*, not how many
bits they occupy. This matters more than it sounds. RPR 1.0 to 2.0 retypes 510 attributes from the
1.3 `boolean` to `RPRboolean`, an enumeration over an 8-bit type. Both are one byte holding 0 or 1.
Treating "enumeration" as a family of its own reported every one of those 510 as real work.

For the same reason the 1.3 `boolean` is treated as `uint:8` — one byte, which is what it is. That is
not a claim that every boolean is 8 bits: the 1516 MIM's `HLAboolean` is an enumeration over
`HLAinteger32BE`, and correctly comes out as 32.

The **standard MIM datatypes** (`HLAoctet`, `HLAinteger32BE`, `HLAASCIIstring`, …) are built into the
app. A 1516 FOM module never declares them — the RTI adds them — so without this nearly every 1516
datatype would come out unresolved.

Attributes are worked out to the **full** set, everything inherited included, because that is what a
federate publishing the class actually deals with. RPR's
`ObjectRoot.BaseEntity.PhysicalEntity.Platform.Aircraft` declares **zero** attributes and inherits
**45**; listing only what it declares would show an empty class.

</details>

Comparing a `.fed` against a 1516 FOM would otherwise flag every single attribute as a datatype
change, since a FED has no datatypes to offer. The app spots that, reports the names as matching,
and says so once in a note at the top instead of on 600 rows. Register the FED **with its `.omt`**
and the datatype columns become real on both sides.

#### Differences — the full diff

One merged tree of everything Added, Removed, Modified or left Unchanged, with a side-by-side
property table for whichever item you select. Filter by kind of change, search across names and
values, and export to HTML, Markdown or CSV.

**Comparison depth** decides how closely each *matched* item is inspected. It never changes which
items are matched — something added or removed always counts:

| Depth | What it looks at |
| --- | --- |
| Names only | Whether things exist, nothing more |
| **Names + datatypes** *(default)* | What exists, how attributes and parameters are typed, and the datatype definitions themselves |
| Everything | Every OMT property — sharing, ownership, update type, accuracy, prose |

The default is the sweet spot: whether an attribute exists and what it is typed as carries almost
all of the signal for "will these two work together?". The rest is still **shown** in the detail
pane with both values — it just stops adding to the count. Nothing is ever hidden.

Counts are of the actual changes, not their parents. A class whose only change is that one of its
attributes moved is still shown as modified so you can find it, but it is not counted twice. When it
says "18 differences", that is 18 real edits.

#### Stored rows — the raw view

For when you want to see the stored data itself. This queries the database directly and lines the
two FOMs up **table by table**: pick `ObjectAttributes`, `DataTypes`, `Switches` or any of the other
21 tables and see the rows matched up, marked Same / Changed / Added / Removed, with a cell-by-cell
`Column | FOM A | FOM B` panel. Filter to only the rows that differ, search across keys and values,
and export the current table to CSV. Internal ID numbers are resolved for you, so you see
`HLAobjectRoot.Customer.PartySize`, not `ObjectClassId 47`.

---

## Why the two HLA generations don't line up

HLA 1.3 splits its model across **two** files, and the split matters enormously:

- **`.fed`** — Federation Execution Data, the file the 1.3 RTI actually loads. Class structure,
  transportation, order, routing spaces. **No datatypes at all** — an attribute line is only
  `(attribute <name> <transportation> <order> [<space>])`, and the RTI treated the values as opaque
  bytes.
- **`.omt` / `.omd`** — the 1.3 **OMT** document. The paperwork the RTI never read, which *does*
  carry the attribute table: datatypes, cardinality, units, resolution, accuracy, sharing, ownership
  and descriptions. Register one of these and a 1.3 entry can be compared against a 1516 FOM on
  equal terms.

IEEE 1516 merged the two: an Evolved FOM is the documentation *and* what the RTI loads.

| | HLA 1.3 `.fed` | HLA 1.3 `.omt` | IEEE 1516 `.xml` |
| --- | --- | --- | --- |
| Written as | brackets | brackets | XML |
| Top class | `ObjectRoot` | flat IDs + `SuperClass` | `HLAobjectRoot` |
| Datatypes | **none** | complex + enumerated | six tables |
| Attribute type | **none** | yes | yes |
| Sharing | **none** | `PSCapabilities` | `sharing` |
| Units / resolution / accuracy | **none** | yes | on the datatype |

Transportation and order are spelled differently too — `reliable`/`best_effort` and
`timestamp`/`receive` against `HLAreliable`/`HLAbestEffort` and `TimeStamp`/`Receive` — which the
**Normalise transport / order** option folds together.

**Comparing across the two generations is strict.** Anything a 1.3 FED simply cannot express —
datatypes, sharing, ownership, update type, the header block — is reported as a real difference and
carries the reason *"Not expressible in HLA 1.3"*, so you can tell a gap in the format from a change
somebody made on purpose. A 1.3 routing space is likewise flagged *"Not expressible in IEEE 1516;
use dimensions"*.

Two adjustments are on by default, because without them a cross-generation comparison reports every
class twice and is useless. Both can be switched off from the Options row for a literal,
nothing-folded comparison:

- **Match 1.3 / 1516 root names** — `ObjectRoot` ↔ `HLAobjectRoot`, `InteractionRoot` ↔
  `HLAinteractionRoot`, `privilegeToDelete` ↔ `HLAprivilegeToDeleteObject`, `Manager` ↔ `HLAmanager`
- **Normalise transport / order** — `reliable` ↔ `HLAreliable`, `timestamp` ↔ `TimeStamp`, and so on

Both readers are deliberately forgiving, because real files are not clean. One FOM this was tested
against contains an unterminated string that throws off the bracket counting for the whole rest of
the file; the reader notices, falls back to hunting for individually balanced `(Class`,
`(Interaction` and datatype blocks, and recovers the entire model — reporting the damage in
Diagnostics rather than silently losing half the FOM. The XML reader matches elements by name only,
so any of the 1516 namespaces (or none, or a prefix) work, and it reads every value from **either**
an XML attribute **or** a child element, because different tools write the DIF both ways.

### Settings — the app rather than the FOMs

Three things live here, and they are the three that belong to the application rather than to any one
model:

- **Appearance.** Light or dark, applied as you click it.
- **Registry database.** Which library file is open, where it sits, whether it is encrypted, and the
  buttons that open a different one or change its password.
- **About.** Which build this is — version and the commit it came from — and a link to the source.
  The version also sits permanently in the bottom-right corner of the window, where it can be read
  without opening anything; clicking it comes here.

---

## Where your data lives

### The library file

Everything you register goes into a single SQLite database file. The app asks which one to use
rather than assuming, and remembers your answer in `config.json` beside the exe — along with the
libraries you used before it, whether you left the sidebar collapsed, and which theme you chose.

| Situation | What happens |
| --- | --- |
| First run | You are asked: **open an existing** library, or **create a new one**. New ones default to `hlafomreader.db` beside the exe, and can be given a password. |
| A library is remembered | It opens straight away. |
| Remembered but the file is gone | You are told, and offered the picker. It never quietly creates a replacement, because a missing file usually means a disconnected drive rather than a fresh start. |
| Started with `--db <path>` | That library is used, ignoring the config, and is remembered afterwards. |

`config.json` has to live outside the database because it is read *before* any database is open — it
is what names the database to open. Switch libraries at any time with **Open database…** on the
**Settings** screen.

### Those `-wal` and `-shm` files

You will see `yourlibrary.db-wal` and `yourlibrary.db-shm` appear beside your database. They are
normal, and SQLite's doing rather than the app's. Writes land in the `-wal` file first and are folded
back into the main file at checkpoints; the `-shm` file is the index that lets readers find their way
around it. This is what stops a read from blocking while a write is in flight.

Two practical consequences:

- **When you copy or back up a library, take all three files** — unless the app is closed. The `-wal`
  can hold recent registrations that have not reached the `.db` yet.
- They usually disappear on their own when the app closes cleanly. After a crash they linger, and
  SQLite sorts them out the next time it opens the file. Do not delete them by hand unless the app
  shut down properly, or you may throw away real data.

### Passwords

A library can be encrypted with SQLCipher. An encrypted one asks for its password when it opens, and
keeps asking until you get it right or cancel. From **Settings** you can **Set**, **Change** or
**Remove** the password. Each of those rewrites the whole file and only replaces the original once
the new one has been written successfully.

Because of the `-wal` file above, changing a password also folds it back in and clears both sidecars
first — otherwise SQLite would be left holding journal files written under the old password, which it
could not read.

---

## For developers

### How the model is stored

The schema is versioned, foreign keys are on, and the database runs in WAL mode. Parsed content is
spread across `Foms`, `ObjectClasses`, `ObjectAttributes`, `AttributeDimensions`,
`InteractionClasses`, `InteractionParameters`, `InteractionDimensions`, `DataTypes`,
`DataTypeMembers`, `Dimensions`, `RoutingSpaces`, `Transportations`, `Synchronizations`,
`UpdateRates`, `Switches`, `Tags`, `FomNotes`, `TimeRepresentation`, `Diagnostics` and `Comparisons`,
with `Ordinal` columns preserving document order so a reload reproduces the file faithfully. A test
asserts the round trip is diff-clean — a FOM read back out of SQLite must compare identical to the
one that went in.

Those same tables are what the **Stored rows** tab browses. `RegistryTables` holds one hand-authored
`SELECT` per table, each joining away surrogate ids and returning a readable `Key` column that
`TableComparer` uses to align the two sides.

Encryption needs the SQLCipher build of SQLite, so the app references `Microsoft.Data.Sqlite.Core`
plus `SQLitePCLRaw.bundle_e_sqlcipher` rather than the all-in-one `Microsoft.Data.Sqlite` package,
and calls `Batteries_V2.Init()` before the first connection. Connection pooling is switched **off**:
a pooled connection returns to the pool with its key still applied.

### Project layout

```
HLAFomReader.slnx
├─ src/HLAFomReader.Core          net9.0 class library — no WPF dependency
│  ├─ Model/                    the normalised object model shared by both standards
│  ├─ Parsing/                  SExpressionReader + FedParser (1.3), Ieee1516XmlParser, FomFileReader
│  ├─ Comparison/               OmtNormalizer, FomComparer, the diff tree, TableComparer
│  ├─ Registry/                 FomDatabase (schema), SqliteFomRepository, RegistryTables
│  └─ Reporting/                HTML / Markdown / CSV export, XlsxWriter + ClassHierarchyExporter
├─ src/HLAFomReader.App           net9.0-windows WPF app
│  ├─ Themes/                   Precision.Light.xaml + Precision.Dark.xaml (design handoff),
│  │                            Controls.xaml
│  ├─ Views/                    MainWindow, RegistryView, CompareView, StoredRowsView, SettingsView,
│  │                            MessageWindow (the app's own MessageBox replacement)
│  ├─ ViewModels/               MainViewModel, RegistryViewModel, CompareViewModel, StoredRowsViewModel,
│  │                            SettingsViewModel
│  ├─ Infrastructure/           hand-rolled MVVM, DialogService, ThemeManager
│  └─ Converters/
├─ tests/HLAFomReader.Core.Tests  xunit — parsers, comparer, SQLite round trip, stored rows
├─ tests/HLAFomReader.App.Tests   xunit — builds the shell and both screens on an STA thread and
│                               fails on any WPF binding or missing-resource error
└─ samples/                     four Restaurant FOMs across three standards
```

### Build and run

```bash
dotnet run --project src/HLAFomReader.App
```

```bash
dotnet test
```

### Publishing a standalone exe

Produces a single `publish/win-x64/HLAFomReader.exe` (~62 MB) that runs on any 64-bit Windows machine
with **no .NET installed** — copy it anywhere and double-click it.

```bash
dotnet publish src/HLAFomReader.App/HLAFomReader.App.csproj -p:PublishProfile=win-x64
```

> **Do not delete `publish/` before republishing.** The app keeps `config.json` and its database
> *beside the executable*, so if you run the exe straight out of `publish/win-x64/` then wiping that
> folder destroys your library. Publishing over the top replaces the exe and leaves the data alone;
> just close the app first so the file is not locked.
>
> Better still, copy `HLAFomReader.exe` somewhere stable — `C:\Tools\HLAFomReader\` — and run it
> there. Then the build output and your data are never the same folder.

If the target machines already have the **.NET 9 Desktop Runtime**, this builds a ~3 MB exe instead:

```bash
dotnet publish src/HLAFomReader.App/HLAFomReader.App.csproj -p:PublishProfile=win-x64-framework-dependent
```

Both profiles live in `src/HLAFomReader.App/Properties/PublishProfiles/`. Two things there are
deliberate and should not be "fixed":

- **`PublishTrimmed` is not set.** WPF does not support trimming; enabling it yields a binary that
  builds fine and then dies at runtime on XAML type resolution. That is why the self-contained
  output is large.
- **`IncludeNativeLibrariesForSelfExtract` is on.** SQLitePCLRaw ships a native `e_sqlite3.dll`;
  without this it is left loose beside the exe instead of being bundled inside it.

`ReadyToRun` is enabled for faster startup, and both the app and Core build with embedded PDBs, so
the publish output is exactly one file. The version shown on the exe's Details tab in Explorer comes
from the `Version` / `FileVersion` properties in `HLAFomReader.App.csproj` — bump them there before
handing a build to anyone, so two copies can be told apart.

Publishing occasionally fails with `MC2000: Access to the path ...obj\Release\...\HLAFomReader.dll is
denied`, and leaves the **old exe in place without saying so**. A leftover MSBuild process is holding
the markup compiler's temporary assembly. Run `dotnet build-server shutdown`, delete the 0-byte DLL
it named, and publish again — then check the exe's timestamp before believing it worked.

### Theme

The palettes, control metrics and per-state colour mapping come from the *Precision* design handoff.
`Themes/Precision.Light.xaml` and `Themes/Precision.Dark.xaml` are the handoff's two dictionaries
plus a handful of app-specific keys — the diff vocabulary (`StatusChanged`, `StatusRemoved`,
`StatusAdded`, `StatusNeutral`, their on-surface text variants) and the `Scrim` behind a modal.
`Themes/Controls.xaml` recreates the handoff's per-control state tables as WPF `Style` /
`ControlTemplate`. Of the handoff's three theme-toggle treatments only the segmented pair is built:
the theme is set in exactly one place, and the other two were for a switch on the sidebar that would
have been a second control to keep in step with the first.

Three rules keep the pair working, and each of them fails silently rather than at build time:

- **Both dictionaries expose an identical key set.** A style is written once against the key names,
  so a key added to one direction and not the other is a control that renders in one theme and
  disappears in the other. `ThemeTests.TheTwoDirectionsExposeTheSameKeys` is what catches that.
- **Every themed brush is referenced with `{DynamicResource}`.** `ThemeManager.Apply` swaps the
  merged dictionary; a `{StaticResource}` was resolved once at load and keeps painting whichever
  theme happened to be merged at startup. Only the metrics — corner radius, control height, font
  sizes — are `{StaticResource}`, because they are the same in both.
- **The brushes are never recoloured in place.** That shortcut looks tempting and throws: WPF
  freezes a `Freezable` used as a `Setter` value when the style is sealed.
- **Exactly one theme dictionary stays merged.** WPF searches merged dictionaries in reverse order,
  so a stale one left behind sits *after* the new theme and quietly outvotes it — the app writes the
  new setting to disk and does not change colour. `ThemeManager` appends the new dictionary and
  removes every other one, recognising them by a marker key rather than by file name, and
  `ThemeTests` counts what is left.

A colour that works as a chip fill is not automatically a colour that works as text. In the dark
theme one value does both jobs; in the light theme the fills stay bright and carry near-black
labels, and the text variants are darkened to hold contrast on white.

There is no `MessageBox` anywhere in the app, and adding one would undo a good deal of this. A Win32
message box is drawn by the shell rather than by the application, so it cannot follow the theme at
all — it was the single surface that still looked like a different program. `Views/MessageWindow`
replaces it with the same chrome as every other dialog, and `MessageWindow.Show` falls back to a
real `MessageBox` only when the themed window cannot be built, which is the fatal-error path where
delivering the message matters more than how it looks.

`ThemeManager` reads the Windows preference
(`HKCU\…\Themes\Personalize\AppsUseLightTheme`) on a first run and the user's own choice from
`config.json` after that, and applies it in `OnStartup` before any window exists — including the
database picker and the unlock prompt, which can both be the first thing anyone sees.

---

## Samples

> **`samples/` is not in this repository.** Object models can be vendor-supplied, customer-specific
> or export-controlled, so `.fed`, `.omt`, `.omd`, `.fdd` and `.db` files are excluded by
> `.gitignore` as a matter of policy. The test suite reads `samples/` from the repository root, so a
> fresh clone will fail most tests until that folder is restored. Ask whoever owns the working copy
> for it, or point the tests at your own FOMs.

`samples/` holds the same "Restaurant" federation written four ways — see `samples/README.md` for the
full table. Register them all, then try:

- `RestaurantFOM-1516-2010.xml` vs `RestaurantFOM-1516-2010-v2.xml` — same standard, one revision
  apart, twelve deliberate changes
- `RestaurantFOM-1.3.fed` vs `RestaurantFOM-1516-2010.xml` — the cross-generation case
- `RestaurantFOM-1516-2000.xml` vs `RestaurantFOM-1516-2010.xml` — 1516-2000 against Evolved

---

## Known gaps

An honest list of what is not right yet. Worth reading before trusting a clean result.

**1. Arrays ignore how they end — a real bug.** An array is currently boiled down to
`array(<element>,<length>)`, which throws away its `encoding`. So `RPRnullTerminatedArray`
(null-terminated) and `HLAvariableArray` (4-byte length prefix) come out identical when they are not.
This **under-reports** differences, which is the dangerous direction, and should be fixed first.

**2. A record with one field should collapse to that field.** An HLA fixed record holding a single
field encodes as just that field. RPR 1.0's `RTIObjectIdStruct` is a record wrapping one `string`;
RPR 2.0's `RTIobjectId` is a null-terminated `HLAASCIIchar` array. Collapsing the wrapper would turn
roughly 20 of the remaining 46 re-encodings into "same data, verify the encoding once". Note that the
1.3 OMT never states the wire format of `string`, so the files cannot *prove* the two match.

**3. Layout "jumping around" on the Compare screen.** Reported but never reproduced. Making Attribute
data the first tab may have fixed it, since the tabs had different content heights and switching
moved the table. Confirm before chasing it further.

**4. HLA 1.3 enumerations carry no declared width.** A 1.3 OMT lists the allowed values but never says
how big the enumeration is, so `ParameterTypeEnum32` and friends come out as unknown — 24 rows on the
RPR pair. The width could be guessed from the `Enum32` name suffix, but that is a naming habit rather
than anything the standard promises, so it is deliberately not guessed.
