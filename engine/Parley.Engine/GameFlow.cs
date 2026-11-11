// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using UdonSharp;

namespace Parley.Engine
{
    /// <summary>
    /// Runs a game from lobby to finish: who sits where, collecting each power's secret
    /// orders, the negotiation timer, lock-in, resolving the phase, saving it to the
    /// history, players leaving and coming back, pausing, agreed draws, roll-back and
    /// resuming from a saved code or archive.
    ///
    /// It never looks at a clock itself: whoever drives it passes the current time (in
    /// seconds) into the methods that need it. That keeps it deterministic and testable,
    /// and lets the VRChat world feed it the shared network time.
    ///
    /// Orders are handed over as text in the GameCodec order format, one string per
    /// power, and a power's string can only affect that power's own units.
    /// </summary>
    public class GameFlow : UdonSharpBehaviour
    {
        public const int STAGE_LOBBY = 0;     // choosing seats, game not started
        public const int STAGE_ORDERS = 1;    // players are negotiating and entering orders
        public const int STAGE_PAUSED = 2;    // clock stopped (also the state right after a resume)
        public const int STAGE_FINISHED = 3;  // somebody won, or a draw was agreed

        public MapData map;
        public BoardState state;
        public Adjudicator adj;
        public GameCodec codec;
        public GameHistory history;
        public BoardState scratch;

        public int stage;

        /// <summary>Per power: is the player who holds this seat here right now?</summary>
        public bool[] seatPresent;
        /// <summary>Per power: orders handed in for the current phase (GameCodec order text).</summary>
        public string[] submitted;
        /// <summary>Per power: has locked in ("I'm done") for the current phase.</summary>
        public bool[] locked;
        /// <summary>Per power: currently votes to end the game as a draw.</summary>
        public bool[] drawVote;

        /// <summary>Time at which the current phase resolves by itself, or -1 for no deadline.</summary>
        public float deadline;
        /// <summary>While paused: seconds that were left on the clock (-1 = no deadline).</summary>
        public float pausedRemaining;

        /// <summary>If true, a seated player who has left still blocks "everyone is ready".</summary>
        public bool waitForAbsent;
        /// <summary>Goes up by one every time the phase changes; cheap way to notice "something happened".</summary>
        public int phaseSerial;
        /// <summary>Largest archive this game may grow to, in characters (0 = unlimited).</summary>
        public int maxArchiveChars;

        public string error = "";

        public void Init(MapData m, BoardState s, Adjudicator a, GameCodec c, GameHistory h, BoardState scratchState)
        {
            map = m; state = s; adj = a; codec = c; history = h; scratch = scratchState;
            int n = m.powerCount;
            seatPresent = new bool[n]; submitted = new string[n]; locked = new bool[n]; drawVote = new bool[n];
            stage = STAGE_LOBBY; deadline = -1; pausedRemaining = -1; phaseSerial = 0;
            for (int i = 0; i < n; i++) { seatPresent[i] = false; submitted[i] = ""; locked[i] = false; drawVote[i] = false; }
            state.SetupStart(map);
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
            string holder = history.seatName[power];
            if (holder != "" && holder != playerName && seatPresent[power]) { error = "that seat is taken"; return false; }
            int old = SeatOf(playerName);
            if (old >= 0 && old != power)
            {
                if (stage != STAGE_LOBBY) { error = "you already play another power"; return false; }
                history.seatName[old] = ""; seatPresent[old] = false;
            }
            history.seatName[power] = playerName; seatPresent[power] = true;
            return true;
        }

        public void ReleaseSeat(int power)
        {
            if (power < 0 || power >= map.powerCount) return;
            history.seatName[power] = ""; seatPresent[power] = false; locked[power] = false; drawVote[power] = false;
        }

        /// <summary>Call when a player leaves the instance. Their seat is kept for them.</summary>
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

        // =====================================================================
        // Starting, saving, loading
        // =====================================================================

        /// <summary>Starts a fresh game from the map's starting position. Unclaimed powers simply hold.</summary>
        public bool StartGame(float now)
        {
            error = "";
            if (stage != STAGE_LOBBY) { error = "a game is already running"; return false; }
            state.SetupStart(map);
            history.count = 0;
            history.Push(codec.EncodeBody(state));
            BeginPhase(now);
            return true;
        }

        /// <summary>Back to the lobby (seats are kept).</summary>
        public void AbandonGame()
        {
            stage = STAGE_LOBBY; deadline = -1; pausedRemaining = -1;
            history.count = 0;
            state.SetupStart(map);
            ClearSubmissions();
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
            ClearSubmissions();
            for (int i = 0; i < map.powerCount; i++) seatPresent[i] = false;
            deadline = -1;
            if (state.phase == BoardState.PHASE_FINISHED) { stage = STAGE_FINISHED; pausedRemaining = -1; }
            else { stage = STAGE_PAUSED; pausedRemaining = PhaseSeconds(); }
            phaseSerial++;
        }

        // =====================================================================
        // Playing a phase
        // =====================================================================

        private float PhaseSeconds()
        {
            int sec = state.phase == BoardState.PHASE_MOVE ? history.moveSeconds
                : (state.phase == BoardState.PHASE_RETREAT ? history.retreatSeconds : history.adjustSeconds);
            return sec > 0 ? sec : -1;
        }

        private void ClearSubmissions()
        {
            for (int i = 0; i < map.powerCount; i++) { submitted[i] = ""; locked[i] = false; drawVote[i] = false; }
        }

        private void BeginPhase(float now)
        {
            ClearSubmissions();
            phaseSerial++;
            if (state.phase == BoardState.PHASE_FINISHED) { stage = STAGE_FINISHED; deadline = -1; return; }
            stage = STAGE_ORDERS;
            float sec = PhaseSeconds();
            deadline = sec > 0 ? now + sec : -1;
        }

        /// <summary>Does this power have any decision to make in the current phase?</summary>
        public bool NeedsOrders(int power)
        {
            if (stage != STAGE_ORDERS && stage != STAGE_PAUSED) return false;
            if (state.phase == BoardState.PHASE_MOVE) return state.CountUnits(power) > 0;
            if (state.phase == BoardState.PHASE_RETREAT) return state.CountDislodged(power) > 0;
            if (state.phase == BoardState.PHASE_ADJUST) return adj.AdjustDelta(state, power) != 0;
            return false;
        }

        /// <summary>
        /// Stores a power's orders for this phase, replacing what it handed in before.
        /// Not allowed once the power has locked in (unlock first).
        /// </summary>
        public bool Submit(int power, string ordersText)
        {
            error = "";
            if (power < 0 || power >= map.powerCount) { error = "no such power"; return false; }
            if (stage != STAGE_ORDERS && stage != STAGE_PAUSED) { error = "orders are not being taken right now"; return false; }
            if (locked[power]) { error = "orders are locked in"; return false; }
            // check the text is well-formed by decoding it into the (cleared) referee
            adj.ClearOrders();
            if (!codec.DecodeOrders(ordersText, state, adj, power)) { adj.ClearOrders(); error = codec.error; return false; }
            adj.ClearOrders();
            submitted[power] = ordersText == null ? "" : ordersText;
            return true;
        }

        public bool SetLocked(int power, bool value)
        {
            error = "";
            if (power < 0 || power >= map.powerCount) { error = "no such power"; return false; }
            if (stage != STAGE_ORDERS && stage != STAGE_PAUSED) { error = "orders are not being taken right now"; return false; }
            locked[power] = value;
            return true;
        }

        /// <summary>True when nobody who still has to decide something is left to wait for.</summary>
        public bool EveryoneReady()
        {
            if (stage != STAGE_ORDERS) return false;
            bool anyone = false;
            for (int p = 0; p < map.powerCount; p++)
            {
                if (!NeedsOrders(p)) continue;
                bool counts = history.seatName[p] != "" && (seatPresent[p] || waitForAbsent);
                if (!counts) continue;
                anyone = true;
                if (!locked[p]) return false;
            }
            return anyone;
        }

        /// <summary>Seconds left on the clock, or -1 when there is no deadline.</summary>
        public float SecondsLeft(float now)
        {
            if (stage == STAGE_PAUSED) return pausedRemaining;
            if (stage != STAGE_ORDERS || deadline < 0) return -1;
            float left = deadline - now;
            return left < 0 ? 0 : left;
        }

        /// <summary>
        /// Call regularly. Resolves the phase when everyone is ready or time is up.
        /// Returns true if the phase was resolved by this call.
        /// </summary>
        public bool Tick(float now)
        {
            if (stage != STAGE_ORDERS) return false;
            bool timeUp = deadline >= 0 && now >= deadline;
            if (!timeUp && !EveryoneReady()) return false;
            ResolveNow(now);
            return true;
        }

        /// <summary>Resolves the current phase immediately with whatever has been handed in.</summary>
        public void ResolveNow(float now)
        {
            if (stage != STAGE_ORDERS && stage != STAGE_PAUSED) return;
            adj.ClearOrders();
            for (int p = 0; p < map.powerCount; p++)
                codec.DecodeOrders(submitted[p], state, adj, p);
            history.SetLatestOrders(codec.EncodeOrders(state, adj, -1));
            adj.Resolve(state);
            history.Push(codec.EncodeBody(state));
            if (maxArchiveChars > 0) history.TrimTo(maxArchiveChars);
            BeginPhase(now);
        }

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

        /// <summary>Changes the clock settings (seconds; 0 = no limit). Takes effect from the next phase.</summary>
        public void SetTimers(int moveSeconds, int retreatSeconds, int adjustSeconds)
        {
            history.moveSeconds = moveSeconds < 0 ? 0 : moveSeconds;
            history.retreatSeconds = retreatSeconds < 0 ? 0 : retreatSeconds;
            history.adjustSeconds = adjustSeconds < 0 ? 0 : adjustSeconds;
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
            if (stage != STAGE_ORDERS && stage != STAGE_PAUSED) return false;
            drawVote[power] = value;
            bool any = false;
            for (int p = 0; p < map.powerCount; p++)
            {
                if (state.IsEliminated(p) || history.seatName[p] == "") continue;
                any = true;
                if (!drawVote[p]) return false;
            }
            if (!any) return false;
            adj.DeclareDraw(state);
            history.Push(codec.EncodeBody(state));
            stage = STAGE_FINISHED; deadline = -1;
            phaseSerial++;
            return true;
        }
    }
}
