# Unity setup and the first-hour proof

For the owner, who is new to Unity. Plain steps, in order. Nobody has opened this
project in Unity yet, so parts of this are **untested**. Anything marked
**[UNSURE]** is from memory of VRChat's workflow; I could not read VRChat's current
docs. If a screen looks different from what is written here, trust the screen and tell
the chat what you saw.

The point of the first hour is to find out cheaply whether the plan works: three small
tests, each ending in a clear pass or fail. Do them before anything else is built.

---

## 1. Install

1. **Make a VRChat account** if you have none, and check that you can log in on the
   VRChat website. **[UNSURE]** VRChat may need your account to have been active for a
   while before it lets you upload worlds (it has a "Trust rank" system). Building and
   testing on your own PC works without it; only uploading may be blocked.
2. **Install the VRChat Creator Companion (VCC)** from vrchat.com/home/download.
   It manages Unity versions and SDK packages for you.
3. **Install the Unity version VCC asks for.** VCC tells you which one when you create
   a project (VRChat pins a specific Unity 2022 release) and offers to install it via
   Unity Hub. Use exactly that version; do not pick another. When Unity Hub asks about
   modules, tick **Android Build Support** (needed for Quest) with its two
   sub-items (OpenJDK, Android SDK & NDK Tools). A free Unity Personal licence is fine.
4. **Create the project.** In VCC: *Create New Project* → template **World** (it
   may be called "VRChat World" / "Worlds - Udon") → name it `ParleyWorld` → pick
   a folder → Create.
5. **Check the packages.** In VCC, open the project → *Manage Project*. You should see
   **VRChat SDK - Worlds** and **VRChat SDK - Base** installed, with the latest
   versions. UdonSharp is included in the current Worlds SDK. **If VCC offers
   UdonSharp as a separate package, add it.** **[UNSURE]** older guides say to
   install it separately; newer ones say it is built in.
6. Click **Open Project**. The first open takes several minutes. When Unity is up you
   should see a menu entry **VRChat SDK** at the top. If you do not, the SDK did not
   install; tell the chat.
7. **Switch to Android later, not now.** Do the PC tests first (Test 1 and the
   two-clients test). Quest comes in Test 2.

## 2. Import the engine

1. In the Unity window, bottom panel **Project**: right-click `Assets` →
   *Create → Folder*, name it `Parley`; inside it another folder `Engine`.
2. On your computer, take the files from `engine/Parley.Engine/` of this repository:
   all the `*.cs` files (Adjudicator, Announcement, BoardState, Dice, GameCodec,
   GameFlow, GameHistory, MapData, OptionGenerator, OrderSeal, PurseRules, Ruleset,
   StandardRules, Visibility). **Do not copy** the `Shim` folder or the `.csproj`
   file; they are only for the normal .NET build and will cause errors in Unity.
3. Drag them into `Assets/Parley/Engine` in the Project panel. Unity then compiles.
   Watch the small spinner at the bottom right.
4. Open the **Console** (menu *Window → General → Console*). With UdonSharp, each
   engine file ending in `: UdonSharpBehaviour` also needs a companion "program asset"
   that Unity makes automatically; **[UNSURE]** in some SDK versions you must click
   each script and press *Create Udon Sharp Program Asset*, or it says so in the
   Inspector. Do it if offered.
5. **Likely errors, and what to do.** I expect a short round of small ones (the engine
   was never compiled against UdonSharp). Most probable:
   - "`X` is not exposed to Udon" / "method not supported": usually a string or
     `Math` call Udon does not offer (candidates: `Split`, `Substring`, `IndexOf`,
     `LastIndexOf`, `Trim`, `ToLower`, `Replace`).
   - UdonSharp complaints about `const`, array handling or `public` fields shared
     between classes.
   - "namespace `Parley`" or `using` lines Udon rejects.
   - Missing-type errors: you copied only some files. Copy all of them.
6. **How to report errors.** In the Console, click an error; the text at the bottom
   of the Console is the full message. Press **Ctrl+A** then **Ctrl+C** in the
   Console's lower text area (or right-click a line → *Copy*) and paste it into a
   chat session, together with which test you were on. Paste **all** red lines, not
   a summary. A chat can usually fix them in one round. Warnings (yellow) can be
   ignored.

---

## 3. The first-hour proof

### Test 1: does the engine compile under UdonSharp?

**What to do:** step 2 above. Nothing else to build.

- **Pass:** the Console has no red errors mentioning files in `Assets/Parley/Engine`,
  after a fresh compile (menu *Assets → Reimport All* if unsure).
- **Fail:** red errors remain after the chat's fixes, or one of them is an
  UdonSharp limit that cannot be worked around (a rule in the engine needing a
  feature Udon lacks). Send the full text. A stuck one goes to a stronger model.

### Test 2: speed on a Quest (map load and one phase)

**Why:** every device loads the map and resolves every phase. If this takes seconds,
the design must spread the work over several frames. It is expected to be tens of
milliseconds for a phase and more for loading the map.

**Prepare the map text.** On your computer, in the repository, run
`./scripts/fetch-reference-maps.sh` (README step 2). That creates
`maps-local/standard.pmap`. Copy the file into Unity as
`Assets/Parley/Maps/standard.pmap.txt` (rename so Unity treats it as a text asset;
**[UNSURE]** Unity only imports `.txt` as text, so the `.txt` ending is on purpose).

**Build the scene:**
1. *File → New Scene* (or use the one in the project), save it as
   `Assets/Parley/Proof.unity`.
2. *GameObject → Create Empty*, name it `Engine`. With it selected, in the Inspector
   press *Add Component* and add **MapData**, **BoardState**, **Adjudicator** (these are the
   engine scripts you imported). Add them all on this one object.
3. *GameObject → Create Empty*, name it `Timing`. Add a new UdonSharp script to it:
   in the Project panel right-click *Create → UdonSharp C# Script*, name it
   `TimingTest`, paste the code below, drag the script onto `Timing`.
4. In the Inspector on `Timing`, fill the fields: drag `Engine` onto `Map`, `State`
   and `Adj`... **each field needs the component, not the object**: drag the object
   `Engine` onto the field; Unity picks the right component. Drag the text asset
   `standard.pmap.txt` onto `MapText`.
5. Add a UI button or interact trigger so something starts it; simplest: the script
   below runs on `Start`, so just enter Play mode or Build & Test.

> **Untested; fix with a chat if it doesn't compile.** Field names of the engine
> classes (`Load`, `Init`, `SetupStart`, `Resolve`, `SetHold`) come from the engine
> source; the VRChat/Unity parts (`Time`, `Debug.Log`) are the likely trouble.

```csharp
using UdonSharp;
using UnityEngine;

public class TimingTest : UdonSharpBehaviour
{
    public MapData Map;
    public BoardState State;
    public Adjudicator Adj;
    public TextAsset MapText;

    void Start()
    {
        float t0 = Time.realtimeSinceStartup;
        bool ok = Map.Load(MapText.text);
        float t1 = Time.realtimeSinceStartup;
        if (!ok) { Debug.Log("PARLEY map load FAILED: " + Map.error); return; }

        State.Init(Map);
        State.SetupStart(Map);
        Adj.Init(Map);
        float t2 = Time.realtimeSinceStartup;

        // One movement phase where every unit holds.
        Adj.ClearOrders();
        for (int u = 0; u < State.unitCount; u++) Adj.SetHold(u);
        Adj.Resolve(State);
        float t3 = Time.realtimeSinceStartup;

        Debug.Log("PARLEY timing: load map " + ((t1 - t0) * 1000f) + " ms, setup "
            + ((t2 - t1) * 1000f) + " ms, resolve one phase " + ((t3 - t2) * 1000f)
            + " ms, units " + State.unitCount);
    }
}
```

**Run it on PC first** (press Play in Unity, read the Console: look for the line
starting `PARLEY timing`). Then on a **Quest**: see section 4 for how to get it onto
the headset, and read the log (see "Reading the log on Quest" below).

- **Pass:** map load under about 500 ms **and** one phase under about 200 ms on Quest.
  (A single hitch like that is fine at the end of a phase.)
- **Soft fail:** map load 0.5 to 5 s, or phase 0.2 to 1 s. The plan still works but the
  load or phase must be spread over frames; tell the chat the numbers.
- **Hard fail:** the map does not load (`FAILED` message; copy it), or either takes
  more than about 5 s, or the Quest freezes or kicks you out of the world.

**Reading the log on Quest. [UNSURE]** The simplest way I know: build with
*Build & Test* to the PC first. For the Quest, a log on the headset needs a cable and
a tool (`adb logcat`) or VRChat's in-game debug log. Easiest alternative: add a
`TextMeshPro` text object in the scene and have the script also write the result into
its `text` field (the chat can add this: ask "show TimingTest output on a world-space
text"), then read it on the headset.

### Test 3: one synced string, including for a late joiner

**Why:** the whole design (sealed orders, host handover, saves) rests on manual-sync
strings arriving at everyone, and at players who join afterwards.

**Build:**
1. *GameObject → 3D Object → Cube*, name it `SyncBox` (a cube, so players can click it:
   the `Interact` event needs a collider). Create the UdonSharp script `SyncTest`
   (code below) and add it to the cube.
2. **Set it to Manual sync:** the script below declares Manual with
   `[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]`; also check in the Inspector
   (**[UNSURE]** where it is shown) that the sync method reads Manual.
3. (Nothing else to wire for clicking.)
4. Add *GameObject → UI → Text - TextMeshPro* (or a 3D text) named `ShowText` and
   drag it into `SyncTest`'s `Show` field. (Unity may ask to import "TMP Essentials":
   say yes.)

> **Untested; fix with a chat if it doesn't compile.**

```csharp
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SyncTest : UdonSharpBehaviour
{
    public TextMeshPro Show;

    [UdonSynced] private string message = "";
    private int counter = 0;

    public override void Interact()
    {
        // Whoever clicks becomes the owner and sets a new string.
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        counter++;
        message = "hello #" + counter + " from "
            + Networking.LocalPlayer.displayName;
        RequestSerialization();      // send it
        Refresh();                   // show it on my own screen too
    }

    public override void OnDeserialization()
    {
        Refresh();                   // a value arrived from the owner
    }

    private void Refresh()
    {
        Show.text = "[" + message.Length + " chars] " + message;
    }
}
```

**Run it (see section 4) with two clients, then:**
1. Player A clicks the cube. **Pass:** player B's text changes to the same message
   within a second or two.
2. Player B clicks the cube. **Pass:** A's text changes to B's message.
3. **Late joiner:** close client B (or have a third client join) **after** A has
   clicked. **Pass:** the new client shows A's last message on its own when it
   arrives, without anyone clicking again. (With two clients, restart B.)
4. Optional size check: have the chat change the script to send a string of 2,000
   then 10,000 characters; note the largest size that still arrives. This number
   decides how big a save/page can be.

- **Pass:** all three of 1 to 3.
- **Fail:** B never sees the text (usually: sync mode not Manual, or the script is not
  on an object with a collider for `Interact`), or the late joiner stays empty. Send the
  exact symptom and the Console text.

---

## 4. Testing with two players

### On one PC (two clients), no upload needed

1. Menu **VRChat SDK → Show Control Panel**. Log in with your VRChat account if asked.
2. Open the **Builder** tab. In the *Offline Testing* section set **Number of Clients**
   to **2** (or more) and make sure **Force Non-VR** is ticked (so the clients start on
   the desktop without a headset).
3. Press **Build & Test**. Unity builds the world and starts the clients. **[UNSURE]**
   This needs VRChat installed on the same PC (Steam or the Meta/Oculus version) and
   the SDK may ask where the VRChat program is. If it complains, set the path in the
   SDK's settings.
4. Two VRChat windows open in the same local test world. Use one window as player A,
   the other as player B (click into a window to control it). **[UNSURE]** both
   clients are the same account but are separate users to the world, which is enough
   for sync tests. Features that need distinct accounts (saves in PlayerData across
   people) are checked later.
5. To test a late joiner: close one window, then start another with Build & Test's
   *Launch* again (or relaunch from the same panel); it will join late.

### On a Quest (private upload)

1. In VCC, open the project's package list and make sure it is on the Android build
   target: Unity menu *File → Build Settings → Android → Switch Platform* (slow, needs
   the Android module from step 1.3). The VRChat SDK panel will also show the
   build target.
2. VRChat SDK Control Panel → **Authentication**: log in. **Builder** tab: fix any
   issues it lists (a Quest world has limits on size and shaders; the proof scene is
   tiny, so it should be clean).
3. **Build & Publish for Android**. Give the world a name, set it **Private**
   (visible to you only), accept the terms. **[UNSURE]** a first upload needs the
   account to be allowed to upload. If it is refused, tell the chat the message.
4. **To test two headsets/people:** on the VRChat website open your uploaded world and
   **Create Instance → Invite Only** (or Friends), then invite your second device or
   friend. For a PC-plus-Quest test, build the **Windows** version too (**[UNSURE]**
   the SDK may need both builds attached to the same world ID: use *Build & Publish for
   Windows* on the same world entry, then *Android*, then both platforms join the same
   instance).
5. Remember: every upload goes onto your account; delete test worlds from the VRChat
   website when finished.

---

## 5. What to send back to the next chat session

Paste this into a new session with the repository open, filled in:

```
Test 1 (compile under UdonSharp): PASS / FAIL
  Console errors (full text, all red lines): ...
Test 2 (Quest speed): PASS / SOFT FAIL / HARD FAIL
  PARLEY timing line on PC:    ...
  PARLEY timing line on Quest: ...   (or: could not read the Quest log)
Test 3 (synced string): PASS / FAIL
  A -> B arrives: yes/no   B -> A arrives: yes/no   late joiner gets it: yes/no
  Largest string that arrived (if tried): ...
Setup notes: Unity version used: ...  SDK version: ...  anything that
  differed from this document (menu names, steps that did not work): ...
```

Also send: screenshots of any screen that confused you, and which steps in this
document were wrong. The chat will update this file with what really works.

After the three tests, the remaining questions in the table at the end of
`docs/vrchat-sync-design.md` (hash speed, owner-leaves behaviour, PlayerData limits) are
checked next, with a small script each, in the order listed there.
