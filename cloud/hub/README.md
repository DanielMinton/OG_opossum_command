# Opossum Hub

One app on your phone and in Teams that shows what the work PC reported, what needs you, what people
asked you for, and how the day went. It reads the three SharePoint lists the flows fill
(`cloud/lists/`) and your Microsoft To Do list **Requests**. It can't start anything on the PC.
Everything stays in your work tenant. Nothing here uses a premium connector: a premium licence
just to read my own status file would be a hard sell.

| Screen | Reads | The only thing it writes |
|---|---|---|
| **Now** | `Opossum Snapshot` (the `status` row, including `Raw JSON`), latest `Opossum Wraps` row | nothing |
| **Alerts** | `Opossum Alerts`, newest first; Open (New + Seen) or All | **Got it** sets `State` to `Seen` |
| **Asks** | To Do list `Requests`, live (not copied) | **Done** sets the task to `completed` |
| **Day** | `Opossum Wraps`, the chosen day plus the last 30 | nothing |

Team mode (Planner, the Team tab, "Share with team") isn't in this version: it needs the team to agree
first. The blueprint's **Explain** (AI) and **Tomorrow** buttons are left out too.

Two ways to build it:

- **`powerapps/`**: the real Hub, a Power Apps canvas app, as Power Fx YAML you paste into Power Apps
  Studio. About 5 minutes. Steps below.
- **`sharepoint-page.json`**: a read-only fallback if Power Apps is turned off for you. It's one
  SharePoint page with three list web parts. See [The SharePoint page fallback](#the-sharepoint-page-fallback).

## Before you start

- Use your **work** account in your **work** environment only. Never a personal account or tenant.
- The three lists exist on your site (`cloud/lists/*.json`). The Hub uses their **display names**:
  `'Generated at'`, `'Completed today'`, `'Failed (24 h)'`, `'Needs attention'`, `'Running now'`,
  `'Next scheduled'`, `'Next scheduled workflow'`, `'Raw JSON'` (Snapshot); `'Workflow name'`,
  `Event`, `Workflow`, `Step`, `Message`, `At`, `State`, `'Resolved at'` (Alerts); `Date`, `Done`, `Open`,
  `Overdue`, `Waiting`, `'For date'`, `Tomorrow`, `Summary` (Wraps). If you renamed a column, rename it
  in the YAML too.
- A To Do list named exactly **Requests** exists (the Request Catcher flow uses it). Until it does,
  Asks says so and shows nothing.

## Paste it into Studio (5 minutes)

1. Go to **make.powerapps.com**, signed in with your work account, in your work environment.
   **+ Create → Blank app → Blank canvas app**. Name it `Opossum Hub`, format **Phone**, then
   **Create**.
2. **Settings → Updates → New**: make sure **Modern controls and themes** is on. It's on by default
   for new apps. The YAML uses the modern **Text** and **Button** controls (`ModernText@1.0.0`,
   `ModernButton@1.0.0`). Then **Settings → Display**: turn **Scale to fit** off so the screens fill a
   Teams tab and a phone. The layout uses auto-layout containers.
3. **Data → Add data**:
   - **SharePoint** → **Connect directly** → your site (`__SITE_URL__`) → tick **Opossum Alerts**,
     **Opossum Snapshot**, **Opossum Wraps** → **Connect**.
   - **Microsoft To-Do (Business)** → **Connect**. Check the name the Data pane gives it. The YAML
     calls it `MicrosoftToDoBusiness`. If the Data pane shows another name, replace every
     `MicrosoftToDoBusiness` in `AsksScreen.pa.yaml` with that name before you paste.
   - Add the data **before** pasting. Formulas that name a missing data source paste with errors.
4. Paste the four screens: **NowScreen**, **AlertsScreen**, **AsksScreen**, **DayScreen**. For each
   file in `powerapps/`, open it in a text editor, select everything, and copy. In Studio's **Tree
   view**, right-click the existing screen and choose **Paste** (or press Ctrl+V). The first time,
   the browser asks for clipboard access: allow it for make.powerapps.com.
   - **If Studio won't paste a whole screen** (see [Unsure](#things-to-check-unsure)): add a
     **New screen → Blank** and rename it (for example `NowScreen`). Copy the screen's `Fill` and
     `OnVisible` formulas from the file into the screen's properties. Then copy only the root control,
     from the line `- nowRoot:` (or `- alrRoot:`, `- askRoot:`, `- dayRoot:`) to the end of the file,
     and paste it on that screen. If Studio rejects the indentation, remove the leading spaces first
     (in VS Code: select the lines and press Shift+Tab until `- nowRoot:` starts at column 1).
5. Delete the empty **Screen1**. Select **App** in the Tree view and set **StartScreen** to
   `NowScreen` (`App.pa.yaml`; Studio can't paste the App object).
6. Optional: **Themes → + Add theme** with the primary color `#5457c4` so any control you add later
   matches. The pasted controls already set their colors (blueprint palette, accent `#5457c4`) and
   don't depend on the theme.
7. Press **F5** and check:
   - **Now** shows the PC time and counts. **PC seen** turns amber after 30 minutes without a new
     status during work hours. The red banner shows only when something needs you.
   - **Alerts → Got it** turns a New alert into Seen. Refresh the list in SharePoint to see `State = Seen`.
   - **Asks → Done** removes the task. In To Do it now shows as completed.
   - **Day** shows the last wrap. Tapping an earlier day shows that day's wrap.
8. **Save**, then **Publish**. Fix any red formula errors first (usually a data source or column
   name; see step 3).

## Pin it in Teams and on your phone

1. On make.powerapps.com, open **Apps**, then **⋯** next to *Opossum Hub* → **Add to Teams** →
   **Add to Teams**. Teams opens: choose **Add**. It's added only for you.
2. In Teams, right-click the Opossum Hub icon in the left rail and choose **Pin**.
3. On your phone, open it from Teams (**More** / **Apps** → *Opossum Hub*), or install **Power Apps**
   from the app store, sign in with your work account, and mark the app as a favorite.

Data refreshes when you open a screen. Every header also has a refresh button.

## Things to check (unsure)

The YAML is checked against Microsoft's published `pa.schema.yaml` (v3.0): it parses, the structure
is valid, every formula starts with `=`, and control names are unique. Studio hasn't loaded it yet,
so check these the first time:

- **Pasting a whole screen.** Microsoft documents Paste code for controls. Pasting a `Screens:`
  block as a screen isn't clearly documented. Step 4 has the fallback.
- **Control IDs.** `ModernText@1.0.0` and `ModernButton@1.0.0` are taken from Microsoft's current
  control docs, along with their property names (`Color`, `Size`, `FontWeight`, `Appearance`,
  `BasePaletteColor`, `Radius*`). `GroupContainer` (variants `AutoLayout` / `ManualLayout`) and
  `Gallery` (variant `Vertical`) have no version on purpose, so Studio picks its current one. Their
  variant names and the container property names (`LayoutDirection`, `LayoutAlignItems`, `LayoutGap`,
  `LayoutOverflowY`, `FillPortions`, `DropShadow`) come from Studio's usual output, not from a
  document. If a container pastes as the wrong kind, insert a **Vertical container** by hand and move
  the children into it.
- **To Do in Power Fx.** The operations are from the connector reference (`GetAllTodoListsV2`,
  `ListToDosByFolderV2`, `UpdateToDoV2`, with fields `id`, `title`, `status`, `dueDateTime.dateTime`,
  `body.content`). These aren't confirmed in Studio: the data source name, the optional `{'$top': 200}`
  argument (if it errors, delete it; you then get the first 10 tasks), and whether
  `ListToDosByFolderV2` returns the table directly. If Studio says it returns a record, add `.value`
  after the call.
- **Dates.** SharePoint date/time columns show in the device's time zone (Pacific on your phone and
  PC). To Do due dates are read as their date part only.
- **Light theme only.** The colors are the blueprint's light palette. Teams in dark mode shows the app
  light.

## The SharePoint page fallback

`sharepoint-page.json` lists the ordered SharePoint REST calls that create **Opossum-Hub.aspx** with
a short intro and three **List** web parts, then publish it:

- **Opossum Alerts**, showing its `Open` view (State ne Resolved, newest first; the view is created in
  `cloud/lists/opossum-alerts.json`)
- **Opossum Snapshot**
- **Opossum Wraps**

Run the calls with the SharePoint connector's **Send an HTTP request to SharePoint** (standard), one
action per step, in a flow you start by hand in your work account. Site Address is `__SITE_URL__`.
Copy each response value named in `capture` into the matching placeholder (`__LIST_ID_Alerts__`,
`__VIEW_ID_AlertsOpen__`, `__PAGE_ID__`, …). Fill the placeholders in `canvasContent1` and send it as
one JSON **string** in `CanvasContent1`. `canvasContent1Escaped` is the same thing already turned into
a string.

Unsure here:

- **Site Pages on a personal site.** A OneDrive personal site usually has no Site Pages library.
  Step 0 checks for one. On a 404 the page can't be made there; use the Power Apps Hub instead.
- **The sitepages endpoints are only partly documented.** The create → `SavePageAsDraft` (first save
  names the file after `Title`) → `Publish` order follows what the PnP libraries do. Step 7 checks the
  file name and gives a rename call if it's wrong.
- **List web part properties.** The `webPartData.properties` shape (`selectedListId`,
  `selectedListUrl`, `selectedViewId`, `webpartHeightKey`) can change. If a web part shows nothing,
  add one List web part by hand on a test page, read its `CanvasContent1` (step 9), and copy that
  shape.
- The page is read only. Marking alerts Seen and completing asks need the Power Apps Hub.
