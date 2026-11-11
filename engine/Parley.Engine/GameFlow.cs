// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Runs a game from lobby to finish: who sits where, the negotiation clock, each
    /// power's submit toggle, collecting the secret orders, reading them out, the ready-up
    /// before the next clock starts, players leaving and coming back, pausing, agreed
    /// draws, roll-back and resuming from a saved code or archive.
    ///
    /// A phase goes through these stages:
    ///
    ///   READY     everybody presses Ready (a natural break); then the clock starts
    ///   ORDERS    the clock runs; players work on their sheets and flip "submit" on or off
    ///   PAUSED    the host has stopped the clock (sheets can still be worked on)
    ///   CLOSING   time is up: a few seconds for every device to seal the sheet it has
    ///   REVEAL    seals are frozen; every device opens its sheet
    ///   ANNOUNCE  the orders are read out country by country, then the result is shown
    ///
    /// ORDERS ends early when every player who is here has submitted. With sealed orders
    /// (the normal way to play) nobody's device, including the host's, holds anybody
    /// else's orders until all seals are frozen; see <see cref="OrderSeal"/>. Without
    /// sealing (tests, bots) sheets are handed straight to the flow and CLOSING and
    /// REVEAL are skipped.
    ///
    /// It never looks at a clock itself: whoever drives it passes the current time (in
    /// seconds) into the methods that need it. That keeps it deterministic and testable,
    /// and lets the VRChat world feed it the shared network time.
    ///
    /// Orders are text in the GameCodec order format, one string per power, and a
    /// power's string can only affect that power's own units.
    /// </summary>
    public class GameFlow : UdonSharpBehaviour
    {
        public const int STAGE_LOBBY = 0;     // choosing seats, game not started
        public const int STAGE_ORDERS = 1;    // clock running: negotiating and filling in order sheets
        public const int STAGE_PAUSED = 2;    // clock stopped by the host (also the state right after a resume or roll-back)
        public const int STAGE_FINISHED = 3;  // somebody won, or a draw was agreed
        public const int STAGE_CLOSING = 4;   // sealed orders: last seconds for devices to seal their sheets
        public const int STAGE_REVEAL = 5;    // sealed orders: seals frozen, sheets being opened
        public const int STAGE_ANNOUNCE = 6;  // orders are being read out; the result follows
        public const int STAGE_READY = 7;     // waiting for everyone to press Ready

        public const int READY_NEVER = 0;     // the next clock starts by itself
        public const int READY_MOVEMENT = 1;  // ready-up before every movement (negotiation) phase
        public const int READY_EVERY = 2;     // ready-up before every phase

        public const int CLOSED_ALL_SUBMITTED = 1;
        public const int CLOSED_TIME_UP = 2;
        public const int CLOSED_BY_HOST = 3;

        public MapData map;
        public BoardState state;
        public Adjudicator adj;
        public GameCodec codec;
        public GameHistory history;
        public BoardState scratch;
        public OrderSeal seal;
        public Announcement announce;
        public Ruleset rules;
        public Dice dice;

        public int stage;

        /// <summary>Per power: is the player who holds this seat here right now?</summary>
        public bool[] seatPresent;
        /// <summary>Per power: the order sheet for the current phase, once the flow knows it (GameCodec order text).</summary>
        public string[] sheet;
        /// <summary>Per power: the submit toggle ("I'm done") for the current phase.</summary>
        public bool[] submitted;
        /// <summary>Per power, sealed orders: the seal it has published ("" = none).</summary>
        public string[] sealText;
        /// <summary>Per power, sealed orders: has opened its sheet in the REVEAL stage.</summary>
        public bool[] opened;
        /// <summary>Per power: has pressed Ready (STAGE_READY).</summary>
        public bool[] ready;
        /// <summary>Per power: currently votes to end the game as a draw.</summary>
        public bool[] drawVote;

        // The seals, salts and sheets of the phase that was resolved last, kept so that
        // every device can check afterwards that each sheet matches the seal it saw.
        public string[] lastSeal;
        public string[] lastSalt;
        public string[] lastSheet;
        public string lastSealKey = "";
        /// <summary>The dice seed of the phase that was resolved last ("" if it had no dice).</summary>
        public string lastDiceSeed = "";
        /// <summary>
        /// Games without sealed orders (tests, bots) have no player secrets to build a
        /// dice seed from; whoever runs the game supplies this text instead.
        /// </summary>
        public string diceNoise = "";

        /// <summary>Time at which the clock runs out, or -1 for no deadline (STAGE_ORDERS).</summary>
        public float deadline;
        /// <summary>While paused: seconds that were left on the clock (-1 = no deadline).</summary>
        public float pausedRemaining;
        /// <summary>CLOSING, REVEAL, ANNOUNCE: time at which the stage ends by itself (-1 = waits).</summary>
        public float stageEnds;
        /// <summary>Time at which the current announcement began.</summary>
        public float announceStart;
        /// <summary>Why the last phase closed (CLOSED_*).</summary>
        public int closeReason;

        // ---- settings ----
        /// <summary>True: orders travel as seals and are opened only when all seals are frozen.</summary>
        public bool sealedOrders;
        /// <summary>If true, a seated player who has left still blocks "everyone has submitted".</summary>
        public bool waitForAbsent;
        /// <summary>READY_*: when players must press Ready before the clock starts.</summary>
        public int readyMode;
        /// <summary>Sealed orders: seconds devices get to seal their sheets once time is up.</summary>
        public float closeGraceSeconds;
        /// <summary>Sealed orders: seconds devices get to open their sheets (0 = wait until the host moves on).</summary>
        public float revealSeconds;
        /// <summary>Announcement: the end-of-round sound and the short pause after it.</summary>
        public float announceLeadSeconds;
        /// <summary>Announcement: time to name each country.</summary>
        public float announcePowerSeconds;
        /// <summary>Announcement: time to read each order.</summary>
        public float announceOrderSeconds;
        /// <summary>Announcement: how long the outcome is shown before the game moves on.</summary>
        public float announceResultSeconds;
        /// <summary>Goes up by one every time the position changes; cheap way to notice "something happened".</summary>
        public int phaseSerial;
        /// <summary>Largest archive this game may grow to, in characters (0 = unlimited).</summary>
        public int maxArchiveChars;

        public string error = "";

        private int[] handIn;
        private string[] saltText;

        public void Init(MapData m, BoardState s, Adjudicator a, GameCodec c, GameHistory h, BoardState scratchState, OrderSeal orderSeal, Announcement announcement, Ruleset ruleset, Dice diceCup)
        {
            map = m; state = s; adj = a; codec = c; history = h; scratch = scratchState; seal = orderSeal; announce = announcement;
            rules = ruleset; dice = diceCup;
            rules.Attach(state); rules.Attach(scratch);
            int n = m.powerCount;
            seatPresent = new bool[n]; sheet = new string[n]; submitted = new bool[n]; sealText = new string[n]; opened = new bool[n];
            ready = new bool[n]; drawVote = new bool[n]; handIn = new int[n]; saltText = new string[n];
            lastSeal = new string[n]; lastSalt = new string[n]; lastSheet = new string[n];
            stage = STAGE_LOBBY; deadline = -1; pausedRemaining = -1; stageEnds = -1; announceStart = 0; closeReason = 0; phaseSerial = 0;
            sealedOrders = false; waitForAbsent = false; readyMode = READY_NEVER;
            closeGraceSeconds = 5; revealSeconds = 20;
            announceLeadSeconds = 0; announcePowerSeconds = 0; announceOrderSeconds = 0; announceResultSeconds = 0;
            for (int i = 0; i < n; i++) { seatPresent[i] = false; lastSeal[i] = ""; lastSalt[i] = ""; lastSheet[i] = ""; }
            ClearPhaseData();
            rules.SetupStart(state);
        }

        /// <summary>
        /// The settings for a real live game, as the project owner described it: sealed
        /// orders, 30 minutes of negotiation for the first two movement phases and 15
        /// after that, 2 minutes for retreats, 3 for builds, a spoken announcement, and
        /// a ready-up before every movement phase. Everything can be changed afterwards.
        /// </summary>
        public void UseLiveSettings()
        {
            int[] schedule = new int[3];
            schedule[0] = 1800; schedule[1] = 1800; schedule[2] = 900;
            history.SetMoveSchedule(schedule, 3);
            history.retreatSeconds = 120; history.adjustSeconds = 180;
            sealedOrders = true; readyMode = READY_MOVEMENT;
            closeGraceSeconds = 5; revealSeconds = 20;
            announceLeadSeconds = 3; announcePowerSeconds = 1.5f; announceOrderSeconds = 3; announceResultSeconds = 6;
        }

        // =====================================================================
        // Seats
        // =====================================================================

        /// <summary>The power this player holds, or -1.</summary>
        public int SeatOf(string playerName)
        {
            if (playerName == null || playerName == "") return -1;
            for (int i = 0; i < map.powerCount; i++) if (history.seatName[i] == playerName) return i;
            return -1;
        }

        /// <summary>
        /// Gives a power to a player. Works in the lobby and mid-game (taking over an empty seat
        /// or one whose player has left). A player holds at most one seat.
        /// </summary>
        public bool ClaimSeat(int power, string playerName)
        {
            error = "";
            if (power < 0 || power >= map.powerCount || playerName == null || playerName == "") { error = "no such seat"; return false; }
            if (!rules.Playable(power)) { error = "nobody plays that power"; return false; }
            string holder = history.seatName[power];
            if (holder != "" && holder != playerName && seatPresent[power]) { error = "that seat is taken"; return false; }
            int old = SeatOf(playerName);
            if (old >= 0 && old != power)
            {
                if (stage != STAGE_LOBBY) { error = "you already play another power"; return false; }
                history.seatName[old] = ""; seatPresent[old] = false;
            }
            // a newcomer cannot open the previous player's sealed sheet, so the seat starts the phase afresh
            if (holder != playerName) ClearPower(power);
            history.seatName[power] = playerName; seatPresent[power] = true;
            return true;
        }

        public void ReleaseSeat(int power)
        {
            if (power < 0 || power >= map.powerCount) return;
            history.seatName[power] = ""; seatPresent[power] = false;
            ClearPower(power);
        }

        /// <summary>
        /// Call when a player leaves the instance. Their seat is kept for them; until they
        /// return or somebody takes the seat over, their units simply hold.
        /// </summary>
        public void PlayerLeft(string playerName)
        {
            int p = SeatOf(playerName);
            if (p < 0) return;
            if (stage == STAGE_LOBBY) { history.seatName[p] = ""; }
            seatPresent[p] = false;
        }

        /// <summary>Call when a player arrives. Returns the power they get back, or -1.</summary>
        public int PlayerJoined(string playerName)
        {
            int p = SeatOf(playerName);
            if (p >= 0) seatPresent[p] = true;
            return p;
        }

        public int SeatedCount()
        {
            int c = 0;
            for (int i = 0; i < map.powerCount; i++) if (history.seatName[i] != "") c++;
            return c;
        }

        private bool AnyonePresent()
        {
            for (int i = 0; i < map.powerCount; i++) if (history.seatName[i] != "" && seatPresent[i]) return true;
            return false;
        }

        // =====================================================================
        // Starting, saving, loading
        // =====================================================================

        /// <summary>Starts a fresh game from the map's starting position. Unclaimed powers simply hold.</summary>
        public bool StartGame(float now)
        {
            error = "";
            if (stage != STAGE_LOBBY) { error = "a game is already running"; return false; }
            rules.SetupStart(state);
            history.count = 0;
            history.Push(codec.EncodeBody(state));
            ClearPhaseData();
            announce.Clear();
            phaseSerial++;
            NextStage(now);
            return true;
        }

        /// <summary>Back to the lobby (seats are kept).</summary>
        public void AbandonGame()
        {
            stage = STAGE_LOBBY; deadline = -1; pausedRemaining = -1; stageEnds = -1;
            history.count = 0;
            rules.SetupStart(state);
            ClearPhaseData();
            announce.Clear();
            phaseSerial++;
        }

        /// <summary>The whole game so far as one line of text (see GameHistory).</summary>
        public string Archive() { return history.ToArchive(); }

        /// <summary>The self-contained code of the current position.</summary>
        public string CurrentCode() { return codec.EncodeState(state); }

        /// <summary>
        /// Resumes a game from an archive made by <see cref="Archive"/>: full history, seats and
        /// timer settings. The game comes back paused so everyone can find their seat first;
        /// call PlayerJoined for each player present, then Resume.
        /// </summary>
        public bool LoadArchive(string archive, float now)
        {
            error = "";
            if (!history.LoadArchive(archive, scratch)) { error = history.error; return false; }
            if (history.count == 0) { error = "the archive has no positions"; return false; }
            if (!codec.DecodeBody(history.body[history.count - 1], state)) { error = codec.error; return false; }
            history.orders[history.count - 1] = "";
            AfterLoad();
            return true;
        }

        /// <summary>
        /// Resumes from a single game code (no history, no seat names, timers keep their
        /// current settings). Seats that are already claimed stay claimed.
        /// </summary>
        public bool LoadCode(string code, float now)
        {
            error = "";
            if (!codec.DecodeState(code, scratch)) { error = codec.error; return false; }
            state.CopyFrom(scratch);
            history.count = 0;
            history.Push(codec.EncodeBody(state));
            AfterLoad();
            return true;
        }

        /// <summary>Returns to the start of an earlier phase (index into the history) and continues from there.</summary>
        public bool RollBack(int historyIndex, float now)
        {
            error = "";
            if (historyIndex < 0 || historyIndex >= history.count) { error = "no such phase in the history"; return false; }
            if (!codec.DecodeBody(history.body[historyIndex], scratch)) { error = codec.error; return false; }
            state.CopyFrom(scratch);
            history.RollBackTo(historyIndex);
            AfterLoad();
            return true;
        }

        private void AfterLoad()
        {
            ClearPhaseData();
            announce.Clear();
            for (int i = 0; i < map.powerCount; i++) seatPresent[i] = false;
            deadline = -1; stageEnds = -1;
            if (state.phase == BoardState.PHASE_FINISHED) { stage = STAGE_FINISHED; pausedRemaining = -1; }
            else { stage = STAGE_PAUSED; pausedRemaining = PhaseSeconds(); }
            phaseSerial++;
        }

        // =====================================================================
        // Clock settings
        // =====================================================================

        /// <summary>Which movement phase of the game the board is in (0 = the first).</summary>
        public int MovePhaseIndex()
        {
            return rules.MoveIndex(state);
        }

        /// <summary>Seconds on the clock for the phase the board is in, or -1 for no limit.</summary>
        public float PhaseSeconds()
        {
            int sec = state.phase == BoardState.PHASE_MOVE ? history.MoveSecondsFor(MovePhaseIndex())
                : (state.phase == BoardState.PHASE_RETREAT ? history.retreatSeconds : history.adjustSeconds);
            return sec > 0 ? sec : -1;
        }

        /// <summary>Same clock for every movement phase (seconds; 0 = no limit). Takes effect from the next phase.</summary>
        public void SetTimers(int moveSeconds, int retreatSeconds, int adjustSeconds)
        {
            int[] one = new int[1];
            one[0] = moveSeconds;
            history.SetMoveSchedule(one, 1);
            history.retreatSeconds = retreatSeconds < 0 ? 0 : retreatSeconds;
            history.adjustSeconds = adjustSeconds < 0 ? 0 : adjustSeconds;
        }

        /// <summary>
        /// A schedule for movement phases: seconds for the first, second, ... movement phase;
        /// the last entry applies from then on. For example 1800, 1800, 900.
        /// </summary>
        public void SetMoveSchedule(int[] seconds, int n) { history.SetMoveSchedule(seconds, n); }

        // =====================================================================
        // Playing a phase
        // =====================================================================

        private void ClearPower(int p)
        {
            sheet[p] = ""; submitted[p] = false; sealText[p] = ""; saltText[p] = ""; opened[p] = false; ready[p] = false; drawVote[p] = false;
        }

        private void ClearPhaseData()
        {
            for (int i = 0; i < map.powerCount; i++) ClearPower(i);
        }

        private bool TakingOrders() { return stage == STAGE_ORDERS || stage == STAGE_PAUSED; }

        private bool PhaseOpen()
        {
            return stage == STAGE_ORDERS || stage == STAGE_PAUSED || stage == STAGE_CLOSING || stage == STAGE_REVEAL;
        }

        /// <summary>Does this power have any decision to make in the current phase?</summary>
        public bool NeedsOrders(int power)
        {
            if (!PhaseOpen()) return false;
            return rules.NeedsOrders(state, power);
        }

        /// <summary>
        /// Sealed orders: must this power's device publish a seal this phase? Yes if it has
        /// orders to give, and also, in a phase with dice, if a player sits there at all:
        /// every seal carries a secret that goes into the dice (an empty sheet will do).
        /// </summary>
        public bool NeedsSeal(int power)
        {
            if (!PhaseOpen()) return false;
            if (rules.NeedsOrders(state, power)) return true;
            return rules.UsesDice(state) && history.seatName[power] != "" && rules.Playable(power);
        }

        /// <summary>
        /// Without sealing: stores a power's sheet for this phase, replacing what it handed
        /// in before. Not allowed while the power's submit toggle is on (switch it off first).
        /// </summary>
        public bool Submit(int power, string ordersText)
        {
            error = "";
            if (power < 0 || power >= map.powerCount) { error = "no such power"; return false; }
            if (sealedOrders) { error = "orders are sealed in this game: publish a seal instead"; return false; }
            if (!TakingOrders()) { error = "orders are not being taken right now"; return false; }
            if (submitted[power]) { error = "orders are submitted; switch submit off to change them"; return false; }
            if (!WellFormed(ordersText, power)) return false;
            sheet[power] = ordersText == null ? "" : ordersText;
            return true;
        }

        // checks order text by decoding it into the (cleared) referee
        private bool WellFormed(string ordersText, int power)
        {
            bool ok = rules.OrdersOk(ordersText, state, power);
            if (!ok) error = rules.error;
            return ok;
        }

        /// <summary>
        /// The submit toggle. On means "I am done"; it can be switched off again to keep
        /// editing. With sealed orders, switching on is done by <see cref="SetSeal"/>, and
        /// switching off withdraws the seal.
        /// </summary>
        public bool SetSubmitted(int power, bool value)
        {
            error = "";
            if (power < 0 || power >= map.powerCount) { error = "no such power"; return false; }
            if (!TakingOrders()) { error = "orders are not being taken right now"; return false; }
            if (sealedOrders)
            {
                if (value && sealText[power] == "") { error = "orders are sealed in this game: publish a seal to submit"; return false; }
                if (!value) sealText[power] = "";
            }
            submitted[power] = value;
            return true;
        }

        /// <summary>The text a seal for the current phase must be tied to (see OrderSeal.Make).</summary>
        public string SealKey()
        {
            return history.count.ToString() + "." + codec.EncodeBody(state);
        }

        /// <summary>
        /// Sealed orders: publishes a power's seal, which also switches its submit toggle on.
        /// Allowed while orders are being taken, and during CLOSING for a power that has
        /// not sealed yet (that is the automatic hand-in when the clock reaches zero).
        /// </summary>
        public bool SetSeal(int power, string sealValue)
        {
            error = "";
            if (power < 0 || power >= map.powerCount) { error = "no such power"; return false; }
            if (!sealedOrders) { error = "this game does not use sealed orders"; return false; }
            if (!TakingOrders() && stage != STAGE_CLOSING) { error = "orders are not being taken right now"; return false; }
            if (sealText[power] != "") { error = "orders are submitted; switch submit off to change them"; return false; }
            if (!seal.SealOk(sealValue)) { error = "that is not a seal"; return false; }
            sealText[power] = sealValue; submitted[power] = true;
            return true;
        }

        /// <summary>
        /// Sealed orders, REVEAL stage: a power opens its sheet. Accepted only if salt and
        /// orders match the seal that was frozen when the phase closed.
        /// </summary>
        public bool Open(int power, string salt, string ordersText)
        {
            error = "";
            if (power < 0 || power >= map.powerCount) { error = "no such power"; return false; }
            if (stage != STAGE_REVEAL) { error = "sheets are not being opened right now"; return false; }
            if (sealText[power] == "") { error = "no seal was published for this power"; return false; }
            if (opened[power]) { error = "already opened"; return false; }
            string text = ordersText == null ? "" : ordersText;
            if (!seal.Check(sealText[power], salt, power, SealKey(), text)) { error = "these orders do not match the seal"; return false; }
            if (!WellFormed(text, power)) return false;
            sheet[power] = text; saltText[power] = salt; opened[power] = true;
            return true;
        }

        private bool Counts(int p)
        {
            return history.seatName[p] != "" && (seatPresent[p] || waitForAbsent);
        }

        /// <summary>True when every player who is here and has something to decide has submitted.</summary>
        public bool EveryoneSubmitted()
        {
            if (stage != STAGE_ORDERS) return false;
            if (!AnyonePresent()) return false;
            bool anyone = false;
            for (int p = 0; p < map.powerCount; p++)
            {
                if (!NeedsOrders(p) || !Counts(p)) continue;
                anyone = true;
                if (!submitted[p]) return false;
            }
            // Nobody who is here has anything to decide: a retreat or build phase moves
            // straight on, but a negotiation phase still runs its clock.
            return anyone || state.phase != BoardState.PHASE_MOVE;
        }

        private bool AllSealed()
        {
            for (int p = 0; p < map.powerCount; p++)
                if (NeedsSeal(p) && Counts(p) && sealText[p] == "") return false;
            return true;
        }

        private bool AllOpened()
        {
            for (int p = 0; p < map.powerCount; p++)
                if (sealText[p] != "" && !opened[p] && Counts(p)) return false;
            return true;
        }

        /// <summary>True when every player who is here and still in the game has pressed Ready.</summary>
        public bool EveryoneIsReady()
        {
            if (stage != STAGE_READY) return false;
            bool anyone = false;
            for (int p = 0; p < map.powerCount; p++)
            {
                if (history.seatName[p] == "" || !seatPresent[p] || rules.IsOut(state, p)) continue;
                anyone = true;
                if (!ready[p]) return false;
            }
            return anyone;
        }

        public bool SetReady(int power, bool value)
        {
            error = "";
            if (power < 0 || power >= map.powerCount) { error = "no such power"; return false; }
            if (stage != STAGE_READY) { error = "nobody is being asked to ready up right now"; return false; }
            ready[power] = value;
            return true;
        }

        /// <summary>
        /// Seconds left in the current stage: on the negotiation clock, or until the
        /// closing, opening or announcement ends. -1 when nothing is counting down.
        /// </summary>
        public float SecondsLeft(float now)
        {
            if (stage == STAGE_PAUSED) return pausedRemaining;
            float end = -1;
            if (stage == STAGE_ORDERS) end = deadline;
            else if (stage == STAGE_CLOSING || stage == STAGE_REVEAL || stage == STAGE_ANNOUNCE) end = stageEnds;
            if (end < 0) return -1;
            float left = end - now;
            return left < 0 ? 0 : left;
        }

        /// <summary>
        /// Call regularly (about once a second is plenty). Moves the game on when its
        /// current stage is over. Returns true if the stage changed in this call.
        /// </summary>
        public bool Tick(float now)
        {
            if (stage == STAGE_ORDERS)
            {
                bool timeUp = deadline >= 0 && now >= deadline;
                if (timeUp && !AnyonePresent())
                {
                    // nobody is here: stop the clock rather than play the game out in an empty room
                    pausedRemaining = 30; deadline = -1; stage = STAGE_PAUSED;
                    return true;
                }
                if (timeUp) { Close(now, CLOSED_TIME_UP); return true; }
                if (EveryoneSubmitted()) { Close(now, CLOSED_ALL_SUBMITTED); return true; }
                return false;
            }
            if (stage == STAGE_CLOSING)
            {
                if (!AllSealed() && now < stageEnds) return false;
                BeginReveal(now);
                return true;
            }
            if (stage == STAGE_REVEAL)
            {
                if (!AllOpened() && (stageEnds < 0 || now < stageEnds)) return false;
                ResolveAndAnnounce(now);
                return true;
            }
            if (stage == STAGE_ANNOUNCE)
            {
                if (now < stageEnds) return false;
                NextStage(now);
                return true;
            }
            if (stage == STAGE_READY)
            {
                if (!EveryoneIsReady()) return false;
                BeginOrders(now);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Host override: ends the current stage now. During orders the phase closes with
        /// what is on the sheets; a stuck opening is resolved with the sheets opened so
        /// far; an announcement is skipped; a ready-up starts the clock.
        /// </summary>
        public void ForceNext(float now)
        {
            if (stage == STAGE_ORDERS || stage == STAGE_PAUSED) { deadline = -1; pausedRemaining = -1; Close(now, CLOSED_BY_HOST); }
            else if (stage == STAGE_CLOSING) BeginReveal(now);
            else if (stage == STAGE_REVEAL) ResolveAndAnnounce(now);
            else if (stage == STAGE_ANNOUNCE) NextStage(now);
            else if (stage == STAGE_READY) BeginOrders(now);
        }

        private void Close(float now, int reason)
        {
            closeReason = reason;
            if (!sealedOrders) { ResolveAndAnnounce(now); return; }
            if (AllSealed()) { BeginReveal(now); return; }
            stage = STAGE_CLOSING; deadline = -1;
            stageEnds = now + (closeGraceSeconds < 0 ? 0 : closeGraceSeconds);
        }

        private void BeginReveal(float now)
        {
            bool any = false;
            for (int p = 0; p < map.powerCount; p++) if (sealText[p] != "") any = true;
            if (!any) { ResolveAndAnnounce(now); return; }
            stage = STAGE_REVEAL; deadline = -1;
            stageEnds = revealSeconds > 0 ? now + revealSeconds : -1;
        }

        private int HandIn(int p)
        {
            if (history.seatName[p] == "") return Announcement.S_NOBODY;
            if (!NeedsOrders(p)) return Announcement.S_ORDERS;
            if (sealedOrders)
            {
                if (sealText[p] == "") return seatPresent[p] ? Announcement.S_NONE : Announcement.S_AWAY;
                return opened[p] ? Announcement.S_ORDERS : Announcement.S_UNOPENED;
            }
            return seatPresent[p] || sheet[p] != "" ? Announcement.S_ORDERS : Announcement.S_AWAY;
        }

        private void ResolveAndAnnounce(float now)
        {
            if (!PhaseOpen()) return;
            lastSealKey = sealedOrders ? SealKey() : "";
            for (int p = 0; p < map.powerCount; p++) handIn[p] = HandIn(p);
            lastDiceSeed = DiceSeed();
            rules.ClearOrders();
            for (int p = 0; p < map.powerCount; p++)
            {
                bool use = handIn[p] == Announcement.S_ORDERS;
                if (use) rules.AddOrders(sheet[p], state, p);
                lastSeal[p] = sealText[p];
                lastSheet[p] = use ? sheet[p] : "";
                lastSalt[p] = use && sealedOrders ? saltText[p] : "";
            }
            history.SetLatestOrders(rules.OrdersText(state, -1));
            announce.Capture(state, adj, handIn);
            rules.Resolve(state, lastDiceSeed);
            announce.Finish(adj);
            history.Push(codec.EncodeBody(state));
            if (maxArchiveChars > 0) history.TrimTo(maxArchiveChars);
            ClearPhaseData();
            phaseSerial++;
            deadline = -1; pausedRemaining = -1;
            float length = announce.Duration(announceLeadSeconds, announcePowerSeconds, announceOrderSeconds, announceResultSeconds);
            if (length > 0) { stage = STAGE_ANNOUNCE; announceStart = now; stageEnds = now + length; }
            else NextStage(now);
        }

        // The dice seed of the phase being resolved: a fingerprint of every secret that was
        // opened (each was fixed by its seal before anybody could see anybody else's), tied
        // to the phase. "" when the ruleset rolls no dice.
        private string DiceSeed()
        {
            if (!rules.UsesDice(state)) return "";
            string t = Dice.VERSION + "|seed|" + SealKey();
            if (!sealedOrders) return seal.Sha256Hex(t + "|open|" + diceNoise);
            for (int p = 0; p < map.powerCount; p++)
                if (opened[p]) t += "|" + p.ToString() + ":" + saltText[p];
            return seal.Sha256Hex(t);
        }

        // after a start or an announcement: game over, ready-up, or straight to the clock
        private void NextStage(float now)
        {
            stageEnds = -1;
            if (state.phase == BoardState.PHASE_FINISHED) { stage = STAGE_FINISHED; deadline = -1; return; }
            bool readyUp = readyMode == READY_EVERY || (readyMode == READY_MOVEMENT && state.phase == BoardState.PHASE_MOVE);
            if (!readyUp) { BeginOrders(now); return; }
            for (int p = 0; p < map.powerCount; p++) ready[p] = false;
            stage = STAGE_READY; deadline = -1;
        }

        private void BeginOrders(float now)
        {
            stage = STAGE_ORDERS; stageEnds = -1;
            float sec = PhaseSeconds();
            deadline = sec > 0 ? now + sec : -1;
        }

        /// <summary>
        /// Points <see cref="announce"/>'s Locate result at the place the reading has
        /// reached (use while stage is STAGE_ANNOUNCE).
        /// </summary>
        public void LocateAnnouncement(float now)
        {
            announce.Locate(now - announceStart, announceLeadSeconds, announcePowerSeconds, announceOrderSeconds, announceResultSeconds);
        }

        /// <summary>Stops the clock (host only). Sheets can still be worked on while paused.</summary>
        public void Pause(float now)
        {
            if (stage != STAGE_ORDERS) return;
            pausedRemaining = deadline < 0 ? -1 : (deadline - now < 1 ? 1 : deadline - now);
            deadline = -1;
            stage = STAGE_PAUSED;
        }

        public void Resume(float now)
        {
            if (stage != STAGE_PAUSED) return;
            deadline = pausedRemaining < 0 ? -1 : now + pausedRemaining;
            pausedRemaining = -1;
            stage = STAGE_ORDERS;
        }

        // =====================================================================
        // Draws
        // =====================================================================

        /// <summary>
        /// Records a draw vote. The game ends in a draw once every surviving power with a
        /// player in its seat has voted for it. Votes are cleared when the phase changes.
        /// Returns true if this vote ended the game.
        /// </summary>
        public bool VoteDraw(int power, bool value)
        {
            if (power < 0 || power >= map.powerCount) return false;
            if (!TakingOrders() && stage != STAGE_READY) return false;
            drawVote[power] = value;
            bool any = false;
            for (int p = 0; p < map.powerCount; p++)
            {
                if (rules.IsOut(state, p) || history.seatName[p] == "") continue;
                any = true;
                if (!drawVote[p]) return false;
            }
            if (!any) return false;
            adj.DeclareDraw(state);
            history.Push(codec.EncodeBody(state));
            ClearPhaseData();
            stage = STAGE_FINISHED; deadline = -1; stageEnds = -1;
            phaseSerial++;
            return true;
        }
    }
}
