// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// The networking of one headset in the VRChat world (docs/vrchat-sync-design.md).
    /// It never calls VRChat itself. A thin adapter feeds it what VRChat reports (the
    /// shared clock, who joined or left, who owns which object, the last received text of
    /// every synced object, PlayerData that has loaded) and carries out what it asks for
    /// (texts to write into the objects this device owns, a save into this player's own
    /// PlayerData, ownership requests). Tests run it against a simulated VRChat.
    ///
    /// Every synced object is one string:
    ///
    ///   hub        (owner: the hub)    who hosts, the hub epoch, the session start, the
    ///                                  seats, the archive's length and checksum, and the
    ///                                  live state of the phase in progress
    ///   hubrec     (owner: the hub)    the last few resolutions: for each, the live state
    ///                                  and seats just before the phase was resolved
    ///   page0..    (owner: the hub)    the archive, cut into pages
    ///   slot:NAME  (owner: player)     that player's seat request, seal, opening, ready
    ///
    /// The owner of "hub" runs <see cref="GameFlow"/> for real. Every other device follows:
    /// it loads the hub's live state, and when the hub resolves a phase it resolves the
    /// same phase itself from the hub's record and compares. Sheets and salts exist only
    /// on the player's own device until the hub has frozen the seals.
    /// </summary>
    public class NetNode : UdonSharpBehaviour
    {
        public const string HUB = "hub";
        public const string RECORDS = "hubrec";
        public const string PAGE = "page";
        public const string SLOT = "slot:";
        public const int MAX_PLAYERS = 40;
        public const int MAX_PAGES = 32;
        public const int PAGE_CHARS = 2000;
        public const int KEEP_SEALS = 4;
        public const int KEEP_RECORDS = 3;
        public const int MAX_OUT = 48;
        /// <summary>How long a device waits for the hub's record of a phase before catching up from the pages unchecked.</summary>
        public const int RECORD_WAIT_MS = 8000;

        public GameFlow flow;
        public OrderSeal seal;
        public GameCodec codec;
        public GameHistory history;
        public MapData map;

        /// <summary>This device's player (VRChat display name).</summary>
        public string me = "";
        /// <summary>True while this device owns "hub" and runs the game.</summary>
        public bool isHub;
        /// <summary>The hub's shortest gap between two writes of "hub", in seconds.</summary>
        public float hubWriteSeconds = 1;
        /// <summary>
        /// This device's own sending budget, in bytes a second (text counted at two bytes a
        /// character). Archive pages and records wait for the next second when the budget
        /// is spent; the hub state and the player's slot always go.
        /// </summary>
        public int sendBudgetBytes = 9000;
        /// <summary>The hub rewrites "hub" at least this often even when nothing changed.</summary>
        public float heartbeatSeconds = 10;
        /// <summary>A player's shortest gap between two writes of their slot, in seconds.</summary>
        public float slotWriteSeconds = 0.25f;

        // ---- who is here (my view) ----
        public string[] players;
        public int playerCount;

        // ---- received copies of other devices' objects ----
        public string[] slotName;   // player name of each received slot
        public string[] slotText;
        public int slotCount;
        public string hubText = "";
        public string recText = "";
        public string[] pageText;
        public string[] owner;      // my view of the owner of hub, hubrec, page0.. (index: 0 hub, 1 hubrec, 2+ pages)

        // ---- the hub text I follow ----
        public int maxEpoch = -1;
        public string acceptedHub = "";
        public int acceptedEpoch = -1;
        public int sessionStartMs;
        public bool haveSession;
        public int hubEpoch;
        public int hubWriteMs;
        public int hubArchiveLen;
        public string hubArchiveSum = "";
        public int hubPages;
        public string hubSeats = "";
        public string hubLive = "";
        private bool followDirty;
        private string lastLoaded = "";
        private int stuckEpoch = -1;
        private int stepMs;
        private int waitFor = -1;
        private int waitStartMs;

        // ---- my slot ----
        public int seatReq = -1;
        public string sealPhase = "";
        public string mySeal = "";
        public string openPhase = "";
        public string openSeal = "";
        public string openSalt = "";
        public string openSheet = "";
        public string readyPhase = "";
        public bool myReady;
        private bool slotDirty;
        private int lastSlotMs;
        private bool slotEverWritten;

        /// <summary>The player's order sheet (GameCodec order text). Never leaves this device before the freeze.</summary>
        public string sheetText = "";
        // the last few seals this device made, with their salts and sheets
        public string[] keptSeal;
        public string[] keptSalt;
        public string[] keptSheet;
        public string[] keptPhase;
        private int keptNext;
        // per phase: the seal that sat in my seat when I first saw the phase (not mine)
        public string seenPhase = "";
        public string inheritedSeal = "";
        private string alarmedPhase = "";

        // ---- outputs ----
        /// <summary>Objects to write (id, text). The adapter writes them and clears <see cref="outCount"/>.</summary>
        public string[] outId;
        public string[] outText;
        public int outCount;
        /// <summary>Objects whose ownership this device asks for.</summary>
        public string[] ownWant;
        public int ownWantCount;
        /// <summary>Text to store in this player's PlayerData ("" = nothing to store).</summary>
        public string saveOut = "";
        private string savedSig = "";
        /// <summary>Warnings that name the hub or a player: something was not as it must be.</summary>
        public string[] alarms;
        public int alarmCount;
        /// <summary>Plain notices for the players ("X is now hosting", "your save failed").</summary>
        public string[] notices;
        public int noticeCount;

        // ---- hub side ----
        private int lastHubWriteMs;
        private bool hubWritten;
        private string lastSig = "";
        private string[] pageSent;
        private string[] records;
        private int recordCount;
        private bool recDirty;
        private string cacheKey = "";
        private string cacheArchive = "";
        private int sentSecond = -1;
        private int sentBytes;

        // ---- saves seen this session (D) ----
        public string[] saveName;
        public string[] saveText;
        public int saveCount;

        public void Init(GameFlow gameFlow, string myName)
        {
            flow = gameFlow; seal = flow.seal; codec = flow.codec; history = flow.history; map = flow.map;
            me = myName;
            players = new string[MAX_PLAYERS]; playerCount = 0;
            slotName = new string[MAX_PLAYERS]; slotText = new string[MAX_PLAYERS]; slotCount = 0;
            pageText = new string[MAX_PAGES]; pageSent = new string[MAX_PAGES];
            owner = new string[2 + MAX_PAGES];
            for (int i = 0; i < MAX_PAGES; i++) { pageText[i] = ""; pageSent[i] = ""; }
            for (int i = 0; i < owner.Length; i++) owner[i] = "";
            keptSeal = new string[KEEP_SEALS]; keptSalt = new string[KEEP_SEALS]; keptSheet = new string[KEEP_SEALS]; keptPhase = new string[KEEP_SEALS];
            for (int i = 0; i < KEEP_SEALS; i++) { keptSeal[i] = ""; keptSalt[i] = ""; keptSheet[i] = ""; keptPhase[i] = ""; }
            outId = new string[MAX_OUT]; outText = new string[MAX_OUT]; outCount = 0;
            ownWant = new string[MAX_PAGES + 2]; ownWantCount = 0;
            alarms = new string[64]; alarmCount = 0; notices = new string[64]; noticeCount = 0;
            records = new string[KEEP_RECORDS]; recordCount = 0;
            saveName = new string[MAX_PLAYERS]; saveText = new string[MAX_PLAYERS]; saveCount = 0;
            slotDirty = true;
        }

        // =====================================================================
        // Inputs from VRChat
        // =====================================================================

        /// <summary>Unpredictable noise for this device's salts (head and hand positions, tap times).</summary>
        public void Stir(string noise) { seal.Stir(noise); flow.Stir(noise); }

        public void OnPlayerJoined(string name)
        {
            if (FindPlayer(name) < 0 && playerCount < MAX_PLAYERS) { players[playerCount] = name; playerCount++; }
            if (isHub) flow.PlayerJoined(name);
        }

        public void OnPlayerLeft(string name)
        {
            int i = FindPlayer(name);
            if (i >= 0)
            {
                for (int k = i + 1; k < playerCount; k++) players[k - 1] = players[k];
                playerCount--;
            }
            // the slot copy is kept: a sheet its player opened before leaving still counts
            if (isHub) flow.PlayerLeft(name);
        }

        /// <summary>VRChat says this object now belongs to this player.</summary>
        public void OnOwner(string objectId, string ownerName)
        {
            int k = ObjIndex(objectId);
            if (k < 0) return;
            owner[k] = ownerName;
            if (k == 0)
            {
                bool mine = ownerName == me;
                if (mine && !isHub) BecomeHub();
                else if (!mine && isHub)
                {
                    // what I received before I was hub is older than my own state: follow only what the new hub sends
                    isHub = false; hubText = ""; hubLive = ""; followDirty = false; lastLoaded = "";
                    Notice(ownerName + " is now hosting");
                }
            }
            else if (ownerName == me && isHub)
            {
                // a page or the record object came to me: write it afresh
                if (k == 1) recDirty = true; else pageSent[k - 2] = "";
            }
        }

        /// <summary>The text of a synced object as just received (VRChat's OnDeserialization).</summary>
        public void OnReceive(string objectId, string text)
        {
            if (text == null) text = "";
            if (objectId == HUB) { TakeHub(text); return; }
            if (objectId == RECORDS) { if (!isHub) { recText = text; followDirty = true; } return; }
            int k = ObjIndex(objectId);
            if (k >= 2) { if (!isHub) { pageText[k - 2] = text; followDirty = true; } return; }
            if (objectId.Length > SLOT.Length && objectId.Substring(0, SLOT.Length) == SLOT)
            {
                string name = history.UnescapeName(objectId.Substring(SLOT.Length));
                if (name == me) return;
                int s = FindSlot(name);
                if (s < 0)
                {
                    if (slotCount >= MAX_PLAYERS) return;
                    s = slotCount; slotCount++; slotName[s] = name;
                }
                slotText[s] = text;
            }
        }

        /// <summary>A player's PlayerData has loaded or changed ("" if they have no save).</summary>
        public void OnPlayerData(string name, string text)
        {
            int i = -1;
            for (int k = 0; k < saveCount; k++) if (saveName[k] == name) i = k;
            if (i < 0) { if (saveCount >= MAX_PLAYERS) return; i = saveCount; saveCount++; saveName[i] = name; }
            saveText[i] = text == null ? "" : text;
        }

        /// <summary>Whether the last save into this player's PlayerData worked.</summary>
        public void OnSaveResult(bool ok)
        {
            if (!ok) Notice("your save could not be stored; the game goes on, and the others' saves are unaffected");
        }

        private void TakeHub(string text)
        {
            if (isHub) return;
            string[] f = text.Split('\n');
            if (f.Length != 10 || f[0] != "GH1") return;
            string hub = history.UnescapeName(f[1]);
            int ep = map.ParseInt(f[2], -1);
            if (ep < 0) return;
            // A send from a hub that has been replaced can still arrive: ignore anything
            // older than what was already accepted. Equal epochs (two devices that took
            // over at once) go to whoever VRChat says owns the object.
            if (ep < maxEpoch) return;
            if (ep == maxEpoch && hub != acceptedHub && hub != owner[0]) return;
            maxEpoch = ep; acceptedHub = hub; hubEpoch = ep;
            sessionStartMs = map.ParseInt(f[3], 0); haveSession = true;
            hubWriteMs = map.ParseInt(f[4], 0);
            hubArchiveLen = map.ParseInt(f[5], -1); hubArchiveSum = f[6]; hubPages = map.ParseInt(f[7], 0);
            hubSeats = f[8]; hubLive = f[9];
            hubText = text;
            followDirty = true;
        }

        // =====================================================================
        // Local player's actions
        // =====================================================================

        public void SetSheet(string ordersText) { sheetText = ordersText == null ? "" : ordersText; }

        public int MyPower() { return flow.SeatOf(me); }

        public string PhaseId() { return flow.timeline + "." + history.count.ToString(); }

        /// <summary>The submit toggle on: seals the sheet as it is now and publishes only the seal.</summary>
        public bool SubmitOn()
        {
            int p = MyPower();
            if (p < 0 || !flow.NeedsSeal(p)) return false;
            int st = flow.stage;
            string pid = PhaseId();
            if (st != GameFlow.STAGE_ORDERS && st != GameFlow.STAGE_PAUSED && st != GameFlow.STAGE_CLOSING) return false;
            if (st == GameFlow.STAGE_CLOSING && sealPhase == pid && mySeal != "") return false;
            string salt = seal.NextSalt();
            string s = seal.Make(salt, p, flow.SealKey(), sheetText);
            keptSeal[keptNext] = s; keptSalt[keptNext] = salt; keptSheet[keptNext] = sheetText; keptPhase[keptNext] = pid;
            keptNext = (keptNext + 1) % KEEP_SEALS;
            sealPhase = pid; mySeal = s; slotDirty = true;
            return true;
        }

        /// <summary>The submit toggle off: withdraws the seal so the sheet can be edited.</summary>
        public bool SubmitOff()
        {
            if (flow.stage != GameFlow.STAGE_ORDERS && flow.stage != GameFlow.STAGE_PAUSED) return false;
            sealPhase = PhaseId(); mySeal = ""; slotDirty = true;
            return true;
        }

        public bool IsSubmitted() { return sealPhase == PhaseId() && mySeal != ""; }

        public void PressReady(bool value) { readyPhase = PhaseId(); myReady = value; slotDirty = true; }

        public void RequestSeat(int power) { seatReq = power; slotDirty = true; }

        /// <summary>"Host here": asks VRChat for the hub objects.</summary>
        public void HostHere() { WantOwn(HUB); }

        // host-only actions (ignored on other devices)
        public bool HostStart(int nowMs)
        {
            if (!isHub || !flow.StartGame(Now(nowMs))) return false;
            Canonical();
            return true;
        }
        public void HostPause(int nowMs) { if (isHub) flow.Pause(Now(nowMs)); }
        public void HostResume(int nowMs) { if (isHub) flow.Resume(Now(nowMs)); }
        public void HostForceNext(int nowMs) { if (isHub) HubTick(Now(nowMs), true); }

        /// <summary>Host: continue a saved game (an archive from somebody's PlayerData). It comes back paused.</summary>
        public bool HostLoad(string archive, int nowMs)
        {
            if (!isHub) return false;
            float now = Now(nowMs);
            if (!flow.LoadArchive(archive, now)) return false;
            recordCount = 0; recDirty = true;
            for (int i = 0; i < playerCount; i++) flow.PlayerJoined(players[i]);
            return true;
        }

        // =====================================================================
        // Every frame (or a few times a second)
        // =====================================================================

        public float Now(int nowMs) { return (nowMs - sessionStartMs) / 1000f; }

        public void Step(int nowMs)
        {
            stepMs = nowMs;
            int second = nowMs / 1000;
            if (second != sentSecond) { sentSecond = second; sentBytes = 0; }
            if (isHub)
            {
                if (!haveSession) { sessionStartMs = nowMs; haveSession = true; }
                HubTick(Now(nowMs), false);
            }
            else if (followDirty && haveSession && hubEpoch != stuckEpoch)
            {
                followDirty = false;
                Follow();
            }
            if (haveSession) PlayerStep(Now(nowMs));
            if (isHub) HubPublish(nowMs);
            if (slotDirty && (!slotEverWritten || nowMs - lastSlotMs >= (int)(slotWriteSeconds * 1000)))
            {
                Put(SLOT + history.EscapeName(me), MySlot());
                slotDirty = false; slotEverWritten = true; lastSlotMs = nowMs;
            }
            SaveIfNew();
        }

        // ---- the player's own device: auto-seal at the deadline, open after the freeze ----
        private void PlayerStep(float now)
        {
            int p = MyPower();
            string pid = PhaseId();
            if (pid != seenPhase)
            {
                seenPhase = pid;
                inheritedSeal = "";
                if (p >= 0 && flow.sealText[p] != "" && FindKept(flow.sealText[p]) < 0)
                {
                    inheritedSeal = flow.sealText[p];
                    // a seal from before my headset restarted (or from the seat's previous
                    // player): it can never be opened, so withdraw it while that is possible
                    if (flow.stage == GameFlow.STAGE_ORDERS || flow.stage == GameFlow.STAGE_PAUSED) { sealPhase = pid; mySeal = ""; slotDirty = true; }
                }
            }
            if (p < 0) return;
            int st = flow.stage;
            bool timeUp = (st == GameFlow.STAGE_ORDERS && flow.deadline >= 0 && now >= flow.deadline) || st == GameFlow.STAGE_CLOSING;
            if (timeUp && flow.NeedsSeal(p) && !(sealPhase == pid && mySeal != "") && openPhase != pid) SubmitOn();
            if (st == GameFlow.STAGE_REVEAL) CheckFrozen(p, pid, flow.sealText[p], true);
        }

        // My seat's frozen seal: open it if it is mine; alarm if it is a seal I never made.
        private void CheckFrozen(int p, string pid, string frozen, bool open)
        {
            if (frozen == "" || (openPhase == pid && openSeal == frozen)) return;
            int k = FindKept(frozen);
            if (k >= 0)
            {
                if (open && keptPhase[k] == pid)
                {
                    openPhase = pid; openSeal = frozen; openSalt = keptSalt[k]; openSheet = keptSheet[k]; slotDirty = true;
                }
                return;
            }
            if (frozen == inheritedSeal || alarmedPhase == pid) return;
            alarmedPhase = pid;
            Alarm(acceptedHub + " froze a seal for " + map.powerName[p] + " that this device never made");
        }

        // =====================================================================
        // The hub
        // =====================================================================

        private void BecomeHub()
        {
            // first catch up with whatever the old hub sent last
            if (haveSession && hubText != "" && hubEpoch != stuckEpoch) Follow();
            isHub = true;
            maxEpoch = maxEpoch + 1; hubEpoch = maxEpoch; acceptedHub = me; acceptedEpoch = hubEpoch;
            for (int p = 0; p < map.powerCount; p++)
                flow.seatPresent[p] = history.seatName[p] != "" && FindPlayer(history.seatName[p]) >= 0;
            // keep the records I received, so a device that missed a resolution can still check it
            recordCount = 0;
            if (recText != "")
            {
                string[] r = recText.Split('\t');
                for (int i = 1; i < r.Length && recordCount < KEEP_RECORDS; i++) { records[recordCount] = r[i]; recordCount++; }
            }
            recDirty = true; hubWritten = false; lastSig = "";
            for (int i = 0; i < MAX_PAGES; i++) pageSent[i] = "";
            WantOwn(RECORDS);
            for (int i = 0; i < MAX_PAGES; i++) WantOwn(PAGE + i.ToString());
            Notice(me + " is now hosting");
        }

        private void HubTick(float now, bool force)
        {
            ReadSlots();
            string pre = flow.LiveState(now);
            string preSeats = SeatsText();
            int preCount = history.count;
            string preLine = flow.timeline;
            if (force) flow.ForceNext(now); else flow.Tick(now);
            if (history.count != preCount) Canonical();
            if (history.count == preCount + 1 && flow.timeline == preLine) AddRecord(preCount.ToString() + "~" + preSeats + "~" + Tenths(now) + "~" + pre);
            ReadSlots();
        }

        // Applies what the players' slots say. State-based, so a new hub simply re-reads them.
        private void ReadSlots()
        {
            ApplySlot(me, MySlot(), true);
            for (int i = 0; i < slotCount; i++)
                if (slotText[i] != "") ApplySlot(slotName[i], slotText[i], FindPlayer(slotName[i]) >= 0);
        }

        // A player who has left counts only with a sheet they opened before leaving.
        private void ApplySlot(string name, string text, bool present)
        {
            string[] f = text.Split('\n');
            if (f.Length != 11 || f[0] != "PS1" || history.UnescapeName(f[1]) != name) return;
            int req = map.ParseInt(f[2], -1);
            int p = flow.SeatOf(name);
            if (present && req >= 0 && req != p && flow.ClaimSeat(req, name)) p = req;
            if (p < 0) return;
            string pid = PhaseId();
            string sPhase = f[3]; string sSeal = f[4];
            string oPhase = f[5]; string oSeal = f[6]; string oSalt = f[7]; string oSheet = f[8];
            int st = flow.stage;
            if (!present)
            {
                if (st == GameFlow.STAGE_REVEAL && !flow.opened[p] && oPhase == pid && oSeal != "" && oSeal == flow.sealText[p]) flow.Open(p, oSalt, oSheet);
                return;
            }
            // a sheet already opened under an earlier hub pins the seal it opened
            string want = "";
            bool known = false;
            if (oPhase == pid && oSeal != "") { want = oSeal; known = true; }
            else if (sPhase == pid) { want = sSeal; known = true; }
            if (known && (st == GameFlow.STAGE_ORDERS || st == GameFlow.STAGE_PAUSED) && flow.sealText[p] != want)
            {
                if (flow.sealText[p] != "") flow.SetSubmitted(p, false);
                if (want != "") flow.SetSeal(p, want);
            }
            else if (known && st == GameFlow.STAGE_CLOSING && flow.sealText[p] == "" && want != "") flow.SetSeal(p, want);
            if (st == GameFlow.STAGE_REVEAL && !flow.opened[p] && oPhase == pid && oSeal != "" && oSeal == flow.sealText[p])
                flow.Open(p, oSalt, oSheet);
            if (st == GameFlow.STAGE_READY && f[9] == pid)
            {
                bool r = f[10] == "1";
                if (flow.ready[p] != r) flow.SetReady(p, r);
            }
        }

        private void AddRecord(string rec)
        {
            if (recordCount == KEEP_RECORDS)
            {
                for (int i = 1; i < KEEP_RECORDS; i++) records[i - 1] = records[i];
                recordCount--;
            }
            records[recordCount] = rec; recordCount++;
            recDirty = true;
        }

        private void HubPublish(int nowMs)
        {
            string archive = CachedArchive();
            int pages = (archive.Length + PAGE_CHARS - 1) / PAGE_CHARS;
            if (pages > MAX_PAGES) { pages = MAX_PAGES; Alarm("the archive is too long for the pages; lower maxArchiveChars"); }
            string sum = codec.Checksum(archive);
            string seats = SeatsText();
            string sig = maxEpoch.ToString() + "|" + sum + "|" + seats + "|" + flow.LiveState(0);
            bool changed = sig != lastSig;
            int gap = nowMs - lastHubWriteMs;
            if (!hubWritten || (changed && gap >= (int)(hubWriteSeconds * 1000)) || gap >= (int)(heartbeatSeconds * 1000))
            {
                string t = "GH1\n" + history.EscapeName(me) + "\n" + hubEpoch.ToString() + "\n" + sessionStartMs.ToString() + "\n" + nowMs.ToString()
                    + "\n" + archive.Length.ToString() + "\n" + sum + "\n" + pages.ToString() + "\n" + seats + "\n" + flow.LiveState(Now(nowMs));
                Put(HUB, t);
                hubWritten = true; lastSig = sig; lastHubWriteMs = nowMs;
            }
            if (recDirty && owner[1] == me)
            {
                string r = "GR1";
                for (int i = 0; i < recordCount; i++) r += "\t" + records[i];
                if (Fits(r)) { Put(RECORDS, r); recDirty = false; }
            }
            for (int i = 0; i < pages; i++)
            {
                int start = i * PAGE_CHARS;
                int len = archive.Length - start < PAGE_CHARS ? archive.Length - start : PAGE_CHARS;
                string chunk = archive.Substring(start, len);
                if (chunk == pageSent[i] || owner[2 + i] != me) continue;
                if (!Fits(chunk)) break;
                Put(PAGE + i.ToString(), chunk); pageSent[i] = chunk;
            }
        }

        // whether a text still fits in this second's sending budget
        private bool Fits(string text)
        {
            return sentBytes + 2 * text.Length <= sendBudgetBytes;
        }

        private string CachedArchive()
        {
            string key = flow.timeline + "|" + history.count.ToString() + "|" + flow.phaseSerial.ToString() + "|" + SeatNames();
            if (key != cacheKey) { cacheKey = key; cacheArchive = flow.Archive(); }
            return cacheArchive;
        }

        // =====================================================================
        // Following the hub (every device that is not the hub)
        // =====================================================================

        private void Follow()
        {
            if (hubText == "") return;
            string[] lf = hubLive.Split('|');
            if (lf.Length < 8) return;
            string line = lf[1];
            int hc = map.ParseInt(lf[3], -1);
            if (hc < 0) return;
            float writeT = Now(hubWriteMs);

            // the hub resolved phases: check each from its record by resolving it myself
            int guard = 0;
            while (hc > history.count && line == flow.timeline && history.count > 0 && guard < KEEP_RECORDS + 1)
            {
                guard++;
                string rec = FindRecord(history.count, flow.timeline);
                if (rec == "")
                {
                    // the record travels in another object and may come a little later than the hub state
                    if (waitFor != history.count) { waitFor = history.count; waitStartMs = stepMs; }
                    if (stepMs - waitStartMs < RECORD_WAIT_MS) { followDirty = true; return; }
                    break;
                }
                if (!VerifyRecord(rec)) { stuckEpoch = hubEpoch; return; }
            }

            if (hc == history.count)
            {
                MirrorSeats(hubSeats);
                string archive = CachedArchive();
                if (archive.Length == hubArchiveLen && codec.Checksum(archive) == hubArchiveSum)
                {
                    LoadHubLive(lf, writeT);
                    return;
                }
            }
            if (hc < history.count && line == flow.timeline)
            {
                // a new hub that has not caught up yet: wait for it; the same hub going backwards is a rewrite
                if (hubEpoch > acceptedEpoch) return;
                Alarm(acceptedHub + " went back in the game without a roll-back being announced");
                stuckEpoch = hubEpoch;
                return;
            }
            TrySync(lf, writeT, line);
        }

        private void LoadHubLive(string[] lf, float writeT)
        {
            if (hubText == lastLoaded) return;
            int st = map.ParseInt(lf[2], -1);
            if (st == GameFlow.STAGE_ANNOUNCE && flow.stage == GameFlow.STAGE_ANNOUNCE && lf[1] == flow.timeline)
            {
                // I resolved this phase myself and hold the same reading: only take the hub's timing
                int clockT = map.ParseInt(lf[6], -1); int beganT = map.ParseInt(lf[7], 0);
                flow.announceStart = writeT - beganT / 10f;
                flow.stageEnds = clockT < 0 ? -1 : writeT + clockT / 10f;
            }
            else if (!flow.LoadLiveState(hubLive, writeT))
            {
                Alarm(acceptedHub + "'s live state was refused: " + flow.error);
                stuckEpoch = hubEpoch;
                return;
            }
            lastLoaded = hubText;
            acceptedEpoch = hubEpoch;
        }

        private string FindRecord(int count, string line)
        {
            if (recText == "") return "";
            string[] r = recText.Split('\t');
            string prefix = count.ToString() + "~";
            for (int i = 1; i < r.Length; i++)
            {
                if (r[i].Length <= prefix.Length || r[i].Substring(0, prefix.Length) != prefix) continue;
                string[] q = r[i].Split('~');
                if (q.Length != 4) continue;
                string[] lf = q[3].Split('|');
                if (lf.Length > 1 && lf[1] == line) return r[i];
            }
            return "";
        }

        // Resolves the hub's last phase on this device, from the live state the hub had
        // just before resolving it. Every opened sheet in it is checked against its seal.
        private bool VerifyRecord(string rec)
        {
            string[] q = rec.Split('~');
            float t = map.ParseInt(q[2], 0) / 10f;
            MirrorSeats(q[1]);
            if (!flow.LoadLiveState(q[3], t))
            {
                Alarm(acceptedHub + "'s record of the last phase was refused: " + flow.error);
                return false;
            }
            int p = MyPower();
            if (p >= 0) CheckFrozen(p, PhaseId(), flow.sealText[p], false);
            int before = history.count;
            for (int k = 1; k <= 4 && history.count == before; k++) flow.ForceNext(t + 1000 * k);
            if (history.count != before + 1)
            {
                Alarm("could not resolve " + acceptedHub + "'s last phase here");
                return false;
            }
            if (!flow.VerifyLast()) { Alarm(acceptedHub + ": " + flow.error); return false; }
            Canonical();
            // after resolving, the hub's seats are what count (someone may have sat down since)
            string archive = flow.Archive();
            int hc = map.ParseInt(hubLive.Split('|')[3], -1);
            if (hc == history.count)
            {
                MirrorSeats(hubSeats);
                archive = flow.Archive();
                if (archive.Length != hubArchiveLen || codec.Checksum(archive) != hubArchiveSum)
                {
                    Alarm(acceptedHub + " published a result that differs from this device's own resolution");
                    return false;
                }
            }
            acceptedEpoch = hubEpoch;
            return true;
        }

        // Rebuilds the hub's archive from the pages and adopts it, if it may replace mine.
        private void TrySync(string[] lf, float writeT, string line)
        {
            if (hubPages <= 0 || hubPages > MAX_PAGES) return;
            string a = "";
            for (int i = 0; i < hubPages; i++) a += pageText[i];
            if (a.Length != hubArchiveLen || codec.Checksum(a) != hubArchiveSum) return; // pages still on their way
            int verdict = Compare(a);
            if (verdict == 3)
            {
                if (hubEpoch > acceptedEpoch) Notice("the new host resolved the last phase again; its result stands");
                else
                {
                    Alarm(acceptedHub + " rewrote the history of the game");
                    stuckEpoch = hubEpoch;
                    return;
                }
            }
            else if (verdict == 4)
            {
                Alarm(acceptedHub + " rewrote the history of the game");
                stuckEpoch = hubEpoch;
                return;
            }
            else if (verdict == 2) Notice(acceptedHub + " rolled the game back");
            else if (verdict == 5) Notice(acceptedHub + " loaded another game");
            if (!flow.LoadArchive(a, writeT)) { Alarm(acceptedHub + "'s archive was refused: " + flow.error); stuckEpoch = hubEpoch; return; }
            MirrorSeats(hubSeats);
            lastLoaded = "";
            LoadHubLive(lf, writeT);
        }

        // 0 = I have no game yet, 1 = it extends mine, 2 = it is a beginning of mine (roll-back),
        // 3 = differs from mine only in my last phase, 4 = differs earlier, 5 = unrelated game.
        private int Compare(string archive)
        {
            if (history.count == 0) return 0;
            string[] p = archive.Split(';');
            int n = p.Length - 5;
            if (n <= 0) return 5;
            // align on the oldest position both hold (trimming drops the oldest first)
            int offMine = -1; int offTheirs = -1;
            string first = Body(p[4]);
            for (int i = 0; i < history.count && offMine < 0; i++) if (history.body[i] == first) { offMine = i; offTheirs = 0; }
            if (offMine < 0)
                for (int j = 0; j < n && offTheirs < 0; j++) if (Body(p[4 + j]) == history.body[0]) { offTheirs = j; offMine = 0; }
            if (offMine < 0) return 5;
            int k = 0;
            while (offMine + k < history.count && offTheirs + k < n && history.body[offMine + k] == Body(p[4 + offTheirs + k])) k++;
            if (offMine + k == history.count) return 1;
            if (offTheirs + k == n) return 2;
            if (offMine + k == history.count - 1) return 3;
            return 4;
        }

        private string Body(string entry)
        {
            int plus = entry.IndexOf('+');
            return plus < 0 ? entry : entry.Substring(0, plus);
        }

        // =====================================================================
        // Saves
        // =====================================================================

        private void SaveIfNew()
        {
            if (history.count == 0 || MyPower() < 0) return;
            if (!isHub && (hubEpoch == stuckEpoch || acceptedEpoch < 0)) return;
            string sig = flow.timeline + "." + history.count.ToString();
            if (sig == savedSig) return;
            savedSig = sig;
            saveOut = CachedArchive();
        }

        // Every device holds the board exactly as decoded from the latest archived position.
        // The engine lists units in the order it happened to create them, and the order text
        // in the archive follows that order, so without this a hub that started the game
        // and a device that loaded it would write the same orders in a different order.
        private void Canonical()
        {
            if (history.count > 0) codec.DecodeBody(history.body[history.count - 1], flow.state);
        }

        // =====================================================================
        // Small helpers
        // =====================================================================

        private string MySlot()
        {
            return "PS1\n" + history.EscapeName(me) + "\n" + seatReq.ToString() + "\n" + sealPhase + "\n" + mySeal + "\n" + openPhase + "\n" + openSeal
                + "\n" + openSalt + "\n" + openSheet + "\n" + readyPhase + "\n" + (myReady ? "1" : "0");
        }

        private string SeatsText()
        {
            string r = "";
            for (int p = 0; p < map.powerCount; p++)
            {
                if (p > 0) r += ",";
                r += history.EscapeName(history.seatName[p]) + (flow.seatPresent[p] ? "+" : "-");
            }
            return r;
        }

        private string SeatNames()
        {
            string r = "";
            for (int p = 0; p < map.powerCount; p++) r += history.seatName[p] + ",";
            return r;
        }

        private void MirrorSeats(string text)
        {
            string[] s = text.Split(',');
            if (s.Length != map.powerCount) return;
            for (int p = 0; p < map.powerCount; p++)
            {
                if (s[p].Length == 0) continue;
                history.seatName[p] = history.UnescapeName(s[p].Substring(0, s[p].Length - 1));
                flow.seatPresent[p] = s[p][s[p].Length - 1] == '+';
            }
        }

        private int FindPlayer(string name)
        {
            for (int i = 0; i < playerCount; i++) if (players[i] == name) return i;
            return -1;
        }

        private int FindSlot(string name)
        {
            for (int i = 0; i < slotCount; i++) if (slotName[i] == name) return i;
            return -1;
        }

        private string SlotOf(string name)
        {
            int s = FindSlot(name);
            return s < 0 ? "" : slotText[s];
        }

        private int FindKept(string s)
        {
            if (s == "") return -1;
            for (int i = 0; i < KEEP_SEALS; i++) if (keptSeal[i] == s) return i;
            return -1;
        }

        private int ObjIndex(string id)
        {
            if (id == HUB) return 0;
            if (id == RECORDS) return 1;
            if (id.Length > PAGE.Length && id.Substring(0, PAGE.Length) == PAGE)
            {
                int i = map.ParseInt(id.Substring(PAGE.Length), -1);
                if (i >= 0 && i < MAX_PAGES) return 2 + i;
            }
            return -1;
        }

        private void Put(string id, string text)
        {
            sentBytes += 2 * text.Length;
            for (int i = 0; i < outCount; i++) if (outId[i] == id) { outText[i] = text; return; }
            if (outCount >= MAX_OUT) return;
            outId[outCount] = id; outText[outCount] = text; outCount++;
        }

        private void WantOwn(string id)
        {
            for (int i = 0; i < ownWantCount; i++) if (ownWant[i] == id) return;
            if (ownWantCount < ownWant.Length) { ownWant[ownWantCount] = id; ownWantCount++; }
        }

        private string Tenths(float seconds)
        {
            int t = (int)(seconds * 10 + 0.5f);
            return t.ToString();
        }

        private void Alarm(string text)
        {
            for (int i = 0; i < alarmCount; i++) if (alarms[i] == text) return;
            if (alarmCount < alarms.Length) { alarms[alarmCount] = text; alarmCount++; }
        }

        private void Notice(string text)
        {
            if (noticeCount == notices.Length)
            {
                for (int i = 1; i < notices.Length; i++) notices[i - 1] = notices[i];
                noticeCount--;
            }
            notices[noticeCount] = text; noticeCount++;
        }
    }
}
