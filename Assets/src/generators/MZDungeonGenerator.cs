using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/**
 * The default and reference implementation of an {@link IMZDungeonGenerator}.
 */
public class MZDungeonGenerator implements IMZDungeonGenerator, ILogger {

    protected ILogger logger;
    protected long seed;
    protected Random random;
    protected MZDungeon dungeon;
    protected IMZDungeonConstraints constraints;
    protected int maxRetries = 20;

    protected bool bossRoomLocked, GenerateGoal;

    /**
     * Creates a MZDungeonGenerator with a given random seed and places
     * specific constraints on {@link IMZDungeon}s it Generates.
     *
     * @param seed          the random seed to use
     * @param constraints   the constraints to place on generation
     * @see net.bytten.metazelda.constraints.IMZDungeonConstraints
     */
    public MZDungeonGenerator(ILogger logger, long seed,
            IMZDungeonConstraints constraints) {
        this.logger = logger;
        log("MZDungeon seed: "+seed);
        this.seed = seed;
        this.random = new Random(seed);
        assert constraints != null;
        this.constraints = constraints;

        bossRoomLocked = GenerateGoal = true;
    }

    public MZDungeonGenerator(long seed, IMZDungeonConstraints constraints) {
        this(null, seed, constraints);
    }

    @Override
    public void log(String msg) {
        if (logger != null) logger.log(msg);
    }

    public void setMaxRetries(int maxRetries) {
        this.maxRetries = maxRetries;
    }

    /**
     * Randomly chooses a {@link MZRoom} within the given collection that has at
     * least one adjacent empty space.
     *
     * @param roomCollection    the collection of rooms to choose from
     * @return  the room that was chosen, or null if there are no rooms with
     *          adjacent empty spaces
     */
    protected MZRoom chooseRoomWithFreeEdge(List<MZRoom> roomCollection,
            int keyLevel) {
        List<MZRoom> rooms = new List<MZRoom>(roomCollection);
        Collections.shuffle(rooms, random);
        for (int i = 0; i < rooms.Count; ++i) {
            MZRoom room = rooms.Get(i);
            for (Pair<Double,int> next:
                    constraints.GetAdjacentRooms(room.id, keyLevel)) {
                if (dungeon.Get(next.second) == null) {
                    return room;
                }
            }
        }
        return null;
    }

    /**
     * Randomly chooses a {@link Direction} in which the given {@link MZRoom} has
     * an adjacent empty space.
     *
     * @param room  the room
     * @return  the Direction of the empty space chosen adjacent to the MZRoom or
     *          null if there are no adjacent empty spaces
     */
    protected int chooseFreeEdge(MZRoom room, int keyLevel) {
        List<Pair<Double,int>> neighbors = new List<Pair<Double,int>>(
                constraints.GetAdjacentRooms(room.id, keyLevel));
        Collections.shuffle(neighbors, random);
        while (!neighbors.isEmpty()) {
            int choice = RandUtil.choice(random, neighbors);
            if (dungeon.Get(choice) == null)
                return choice;
            neighbors.Remove(choice);
        }
        assert false;
        throw new MZGenerationFailureException("Internal error: MZRoom doesn't have a free edge");
    }

    /**
     * Maps 'keyLevel' to the set of rooms within that keyLevel.
     * <p>
     * A 'keyLevel' is the count of the number of unique keys are needed for all
     * the locks we've placed. For example, all the rooms in keyLevel 0 are
     * accessible without collecting any keys, while to Get to rooms in
     * keyLevel 3, the player must have collected at least 3 keys.
     */
    protected class KeyLevelRoomMapping {
        protected List<List<MZRoom>> map = new List<List<MZRoom>>(
                constraints.GetMaxKeys());

        List<MZRoom> GetRooms(int keyLevel) {
            while (keyLevel >= map.Count) map.Add(null);
            if (map.Get(keyLevel) == null)
                map.Set(keyLevel, new List<MZRoom>());
            return map.Get(keyLevel);
        }

        void AddRoom(int keyLevel, MZRoom room) {
            GetRooms(keyLevel).Add(room);
        }

        int keyCount() {
            return map.Count;
        }
    }

    /**
     * Thrown by several IMZDungeonGenerator methods that can fail.
     * Should be caught and handled in {@link #Generate}.
     */
    protected static class RetryException extends Exception {
    }

    protected static class OutOfRoomsException extends Exception {
    }

    /**
     * Comparator objects for sorting {@link MZRoom}s in a couple of different
     * ways. These are used to determine in which rooms of a given keyLevel it
     * is best to place the next key.
     *
     * @see #placeKeys
     */
    protected static final Comparator<MZRoom>
    EDGE_COUNT_COMPARATOR = new Comparator<MZRoom>() {
        @Override
        public int compare(MZRoom arg0, MZRoom arg1) {
            return arg0.LinkCount() - arg1.LinkCount();
        }
    },
    INTENSITY_COMPARATOR = new Comparator<MZRoom>() {
        @Override
        public int compare(MZRoom arg0, MZRoom arg1) {
            return arg0.GetIntensity() > arg1.GetIntensity() ? -1
                    : arg0.GetIntensity() < arg1.GetIntensity() ? 1
                            : 0;
        }
    };

    /**
     * Sets up the dungeon's entrance room.
     *
     * @param levels    the keyLevel -> room-set mapping to update
     * @see KeyLevelRoomMapping
     */
    protected void initEntranceRoom(KeyLevelRoomMapping levels)
            throws RetryException {
        int id;
        List<int> possibleEntries = new List<int>(
                constraints.initialRooms());
        assert possibleEntries.Count > 0;
        id = possibleEntries.Get(random.nextInt(possibleEntries.Count));

        MZRoom entry = new MZRoom(id, constraints.GetCoords(id), null,
                new MZSymbol(MZSymbol.START), new MZCondition());
        dungeon.Add(entry);

        levels.addRoom(0, entry);
    }

    /**
     * Decides whether to Add a new lock (and keyLevel) at this point.
     *
     * @param keyLevel the number of distinct locks that have been placed into
     *      the map so far
     * @param numRooms the number of rooms at the current keyLevel
     * @param targetRoomsPerLock the number of rooms the generator has chosen
     *      as the target number of rooms to place at each keyLevel (which
     *      subclasses can ignore, if desired).
     */
    protected bool shouldAddNewLock(int keyLevel, int numRooms, int targetRoomsPerLock) {
        int usableKeys = constraints.GetMaxKeys();
        if (IsBossRoomLocked())
            usableKeys -= 1;
        return numRooms >= targetRoomsPerLock && keyLevel < usableKeys;
    }

    /**
     * Fill the dungeon's space with rooms and doors (some locked).
     * Keys are not inserted at this point.
     *
     * @param levels    the keyLevel -> room-set mapping to update
     * @throws RetryException if it fails
     * @see KeyLevelRoomMapping
     */
    protected void placeRooms(KeyLevelRoomMapping levels, int roomsPerLock)
            throws RetryException, OutOfRoomsException {

        // keyLevel: the number of keys required to Get to the new room
        int keyLevel = 0;
        MZSymbol latestKey = null;
        // condition that must hold true for the player to reach the new room
        // (the set of keys they must have).
        MZCondition cond = new MZCondition();

        // Loop to place rooms and link them
        while (dungeon.RoomCount() < constraints.GetMaxRooms()) {

            bool doLock = false;

            // Decide whether we need to place a new lock
            // (Don't place the last lock, since that's reserved for the boss)
            if (shouldAddNewLock(keyLevel, levels.GetRooms(keyLevel).Count, roomsPerLock)) {
                latestKey = new MZSymbol(keyLevel++);
                cond = cond.And(latestKey);
                doLock = true;
            }

            // Find an existing room with a free edge:
            MZRoom parentRoom = null;
            if (!doLock && random.nextInt(10) > 0)
                parentRoom = chooseRoomWithFreeEdge(levels.GetRooms(keyLevel),
                        keyLevel);
            if (parentRoom == null) {
                parentRoom = chooseRoomWithFreeEdge(dungeon.GetRooms(),
                        keyLevel);
                doLock = true;
            }

            if (parentRoom == null)
                throw new OutOfRoomsException();

            // Decide which direction to put the new room in relative to the
            // parent
            int nextId = chooseFreeEdge(parentRoom, keyLevel);
            List<Vector2Int> coords = constraints.GetCoords(nextId);
            MZRoom room = new MZRoom(nextId, coords, parentRoom, null, cond);

            // Add the room to the dungeon
            assert dungeon.Get(room.id) == null;
            synchronized (dungeon) {
                dungeon.Add(room);
                parentRoom.addChild(room);
                dungeon.Link(parentRoom, room, doLock ? latestKey : null);
            }
            levels.addRoom(keyLevel, room);
        }
    }

    /**
     * Places the BOSS and GOAL rooms within the dungeon, in existing rooms.
     * These rooms are moved into the next keyLevel.
     *
     * @param levels    the keyLevel -> room-set mapping to update
     * @throws RetryException if it fails
     * @see KeyLevelRoomMapping
     */
    protected void placeBossGoalRooms(KeyLevelRoomMapping levels)
            throws RetryException {
        List<MZRoom> possibleGoalRooms = new List<MZRoom>(dungeon.RoomCount());

        MZSymbol goalSym = new MZSymbol(MZSymbol.GOAL),
               bossSym = new MZSymbol(MZSymbol.BOSS);

        foreach (MZRoom room in dungeon.GetRooms()) {
            if (room.GetChildren().Count > 0 || room.GetItem() != null)
                continue;
            MZRoom parent = room.GetParent();
            if (parent == null)
                continue;
            if (isGenerateGoal() && (parent.GetChildren().Count != 1 ||
                    !parent.GetPrecond().Implies(room.GetPrecond())))
                continue;
            if (isGenerateGoal()) {
                if (!constraints.roomCanFitItem(room.id, goalSym) ||
                        !constraints.roomCanFitItem(parent.id, bossSym))
                    continue;
            } else {
                if (!constraints.roomCanFitItem(room.id, bossSym))
                    continue;
            }
            possibleGoalRooms.Add(room);
        }

        if (possibleGoalRooms.Count == 0) throw new RetryException();

        MZRoom goalRoom = possibleGoalRooms.Get(random.nextInt(
                possibleGoalRooms.Count)),
             bossRoom = goalRoom.GetParent();

        if (!isGenerateGoal()) {
            bossRoom = goalRoom;
            goalRoom = null;
        }

        if (goalRoom != null) goalRoom.SetItem(goalSym);
        bossRoom.SetItem(bossSym);

        int oldKeyLevel = bossRoom.GetPrecond().GetKeyLevel(),
            newKeyLevel = Math.min(levels.keyCount(), constraints.GetMaxKeys());

        if (oldKeyLevel != newKeyLevel) {
            List<MZRoom> oklRooms = levels.GetRooms(oldKeyLevel);
            if (goalRoom != null) oklRooms.Remove(goalRoom);
            oklRooms.Remove(bossRoom);

            if (goalRoom != null) levels.addRoom(newKeyLevel, goalRoom);
            levels.addRoom(newKeyLevel, bossRoom);

            MZSymbol bossKey = new MZSymbol(newKeyLevel-1);
            MZCondition precond = bossRoom.GetPrecond().And(bossKey);
            bossRoom.SetPrecond(precond);
            if (goalRoom != null) goalRoom.SetPrecond(precond);

            if (newKeyLevel == 0) {
                dungeon.Link(bossRoom.GetParent(), bossRoom);
            } else {
                dungeon.Link(bossRoom.GetParent(), bossRoom, bossKey);
            }
            if (goalRoom != null) dungeon.Link(bossRoom, goalRoom);
        }
    }

    /**
     * Removes the given {@link MZRoom} and all its descendants from the given
     * list.
     *
     * @param rooms the list of MZRooms to remove nodes from
     * @param room  the MZRoom whose descendants to remove from the list
     */
    protected void removeDescendantsFromList(List<MZRoom> rooms, MZRoom room) {
        rooms.Remove(room);
        for (MZRoom child: room.GetChildren()) {
            removeDescendantsFromList(rooms, child);
        }
    }

    /**
     * Adds extra conditions to the given {@link MZRoom}'s preconditions and all
     * of its descendants.
     *
     * @param room  the MZRoom to Add extra preconditions to
     * @param cond  the extra preconditions to Add
     */
    protected void AddPrecond(MZRoom room, MZCondition cond) {
        room.SetPrecond(room.GetPrecond().And(cond));
        for (MZRoom child: room.GetChildren()) {
            AddPrecond(child, cond);
        }
    }

    /**
     * Randomly locks descendant rooms of the given {@link MZRoom} with
     * {@link MZEdge}s that require the switch to be in the given state.
     * <p>
     * If the given state is Either, the required states will be random.
     *
     * @param room          the room whose child to lock
     * @param givenState    the state to require the switch to be in for the
     *                      child rooms to be accessible
     * @return              true if any locks were Added, false if none were
     *                      Added (which can happen due to the way the random
     *                      decisions are made)
     * @see MZCondition.SwitchState
     */
    protected bool switchLockChildRooms(MZRoom room,
            MZCondition.SwitchState givenState) {
        bool anyLocks = false;
        MZCondition.SwitchState state = givenState != MZCondition.SwitchState.Either
                ? givenState
                : (random.nextInt(2) == 0
                    ? MZCondition.SwitchState.On
                    : MZCondition.SwitchState.Off);

        for (MZEdge edge: room.GetEdges()) {
            int neighborId = edge.GetTargetRoomId();
            MZRoom nextRoom = dungeon.Get(neighborId);
            if (room.GetChildren().Contains(nextRoom)) {
                if (room.GetEdge(neighborId).GetSymbol() == null &&
                        random.nextInt(4) != 0) {
                    dungeon.Link(room, nextRoom, state.ToSymbol());
                    AddPrecond(nextRoom, new MZCondition(state.ToSymbol()));
                    anyLocks = true;
                } else {
                    anyLocks |= switchLockChildRooms(nextRoom, state);
                }

                if (givenState == MZCondition.SwitchState.Either) {
                    state = state.Invert();
                }
            }
        }
        return anyLocks;
    }

    /**
     * Returns a path from the goal to the dungeon entrance, along the 'parent'
     * relations.
     *
     * @return  a list of linked {@link MZRoom}s starting with the goal room and
     *          ending with the start room.
     */
    protected List<MZRoom> GetSolutionPath() {
        List<MZRoom> solution = new List<MZRoom>();
        MZRoom room = dungeon.FindGoal();
        while (room != null) {
            solution.Add(room);
            room = room.GetParent();
        }
        return solution;
    }

    /**
     * Makes some {@link MZEdge}s within the dungeon require the dungeon's switch
     * to be in a particular state, and places the switch in a room in the
     * dungeon.
     *
     * @throws RetryException if it fails
     */
    protected void placeSwitches() throws RetryException {
        // Possible TODO: have multiple switches on separate circuits
        // At the moment, we only have one switch per dungeon.
        if (constraints.GetMaxSwitches() <= 0) return;

        List<MZRoom> solution = GetSolutionPath();

        for (int attempt = 0; attempt < 10; ++attempt) {

            List<MZRoom> rooms = new List<MZRoom>(dungeon.GetRooms());
            Collections.shuffle(rooms, random);
            Collections.shuffle(solution, random);

            // Pick a base room from the solution path so that the player
            // will have to encounter a switch-lock to solve the dungeon.
            MZRoom baseRoom = null;
            foreach (MZRoom room in solution) {
                if (room.GetChildren().Count > 1 && room.GetParent() != null) {
                    baseRoom = room;
                    break;
                }
            }
            if (baseRoom == null) throw new RetryException();
            MZCondition baseRoomCond = baseRoom.GetPrecond();

            removeDescendantsFromList(rooms, baseRoom);

            MZSymbol switchSym = new MZSymbol(MZSymbol.Switch);

            MZRoom switchRoom = null;
            foreach (MZRoom room in rooms) {
                if (room.GetItem() == null &&
                        baseRoomCond.Implies(room.GetPrecond()) &&
                        constraints.roomCanFitItem(room.id, switchSym)) {
                    switchRoom = room;
                    break;
                }
            }
            if (switchRoom == null) continue;

            if (switchLockChildRooms(baseRoom, MZCondition.SwitchState.Either)) {
                switchRoom.SetItem(switchSym);
                return;
            }
        }
        throw new RetryException();
    }

    /**
     * Randomly links up some adjacent rooms to make the dungeon graph less of
     * a tree.
     *
     * @throws RetryException if it fails
     */
    protected void graphify() throws RetryException {
        foreach (MZRoom room in dungeon.GetRooms()) {

            if (room.IsGoal() || room.IsBoss()) continue;

            for (Pair<Double,int> next:
                    // Doesn't matter what the keyLevel is; later checks about
                    // preconds ensure linkage doesn't trivialize the puzzle.
                    constraints.GetAdjacentRooms(room.id, Int32.MaxValue)) {
                int nextId = next.second;
                if (room.GetEdge(nextId) != null) continue;

                MZRoom nextRoom = dungeon.Get(nextId);
                if (nextRoom == null || nextRoom.IsGoal() || nextRoom.IsBoss())
                    continue;

                bool forwardImplies = room.GetPrecond().Implies(nextRoom.GetPrecond()),
                        backwardImplies = nextRoom.GetPrecond().Implies(room.GetPrecond());
                if (forwardImplies && backwardImplies) {
                    // both rooms are at the same keyLevel.
                    if (random.nextDouble() >=
                            constraints.edgeGraphifyProbability(room.id, nextRoom.id))
                        continue;

                    dungeon.Link(room, nextRoom);
                } else {
                    MZSymbol difference = room.GetPrecond().SingleSymbolDifference(
                            nextRoom.GetPrecond());
                    if (difference == null || (!difference.IsSwitchState() &&
                            random.nextDouble() >=
                                constraints.edgeGraphifyProbability(room.id, nextRoom.id)))
                        continue;
                    dungeon.Link(room, nextRoom, difference);
                }
            }
        }
    }

    /**
     * Places keys within the dungeon in such a way that the dungeon is
     * guaranteed to be solvable.
     *
     * @param levels    the keyLevel -> room-set mapping to use
     * @throws RetryException if it fails
     * @see KeyLevelRoomMapping
     */
    protected void placeKeys(KeyLevelRoomMapping levels) throws RetryException {
        // Now place the keys. For every key-level but the last one, place a
        // key for the next level in it, preferring rooms with fewest links
        // (dead end rooms).
        for (int key = 0; key < levels.keyCount()-1; ++key) {
            List<MZRoom> rooms = levels.GetRooms(key);

            Collections.shuffle(rooms, random);
            // Collections.sort is stable: it doesn't reorder "equal" elements,
            // which means the shuffling we just did is still useful.
            Collections.sort(rooms, INTENSITY_COMPARATOR);
            // Alternatively, use the EDGE_COUNT_COMPARATOR to put keys at
            // 'dead end' rooms.

            MZSymbol keySym = new MZSymbol(key);

            bool placedKey = false;
            foreach (MZRoom room in rooms) {
                if (room.GetItem() == null && constraints.roomCanFitItem(room.id, keySym)) {
                    room.SetItem(keySym);
                    placedKey = true;
                    break;
                }
            }
            if (!placedKey)
                // there were no rooms into which the key would fit
                throw new RetryException();
        }
    }

    protected static final double
            INTENSITY_GROWTH_JITTER = 0.1,
            INTENSITY_EASE_Off = 0.2;

    /**
     * Recursively applies the given intensity to the given {@link MZRoom}, and
     * higher intensities to each of its descendants that are within the same
     * keyLevel.
     * <p>
     * Intensities set by this method may (will) be outside of the normal range
     * from 0.0 to 1.0. See {@link #normalizeIntensity} to correct this.
     *
     * @param room      the room to set the intensity of
     * @param intensity the value to set intensity to (some randomn variance is
     *                  Added)
     * @see MZRoom
     */
    protected double applyIntensity(MZRoom room, double intensity) {
        intensity *= 1.0 - INTENSITY_GROWTH_JITTER/2.0 +
                INTENSITY_GROWTH_JITTER * random.nextDouble();

        room.SetIntensity(intensity);

        double maxIntensity = intensity;
        for (MZRoom child: room.GetChildren()) {
            if (room.GetPrecond().Implies(child.GetPrecond())) {
                maxIntensity = Math.Max(maxIntensity, applyIntensity(child,
                        intensity + 1.0));
            }
        }

        return maxIntensity;
    }

    /**
     * Scales intensities within the dungeon down so that they all fit within
     * the range 0 <= intensity < 1.0.
     *
     * @see MZRoom
     */
    protected void normalizeIntensity() {
        double maxIntensity = 0.0;
        foreach (MZRoom room in dungeon.GetRooms()) {
            maxIntensity = Math.Max(maxIntensity, room.GetIntensity());
        }
        foreach (MZRoom room in dungeon.GetRooms()) {
            room.SetIntensity(room.GetIntensity() * 0.99 / maxIntensity);
        }
    }

    /**
     * Computes the 'intensity' of each {@link MZRoom}. MZRooms generally Get more
     * intense the deeper they are into the dungeon.
     *
     * @param levels    the keyLevel -> room-set mapping to update
     * @throws RetryException if it fails
     * @see KeyLevelRoomMapping
     * @see MZRoom
     */
    protected void computeIntensity(KeyLevelRoomMapping levels)
            throws RetryException {

        double nextLevelBaseIntensity = 0.0;
        for (int level = 0; level < levels.keyCount(); ++level) {

            double intensity = nextLevelBaseIntensity *
                    (1.0 - INTENSITY_EASE_Off);

            foreach (MZRoom room in levels.GetRooms(level)) {
                if (room.GetParent() == null ||
                        !room.GetParent().GetPrecond().
                            Implies(room.GetPrecond())) {
                    nextLevelBaseIntensity = Math.Max(
                            nextLevelBaseIntensity,
                            applyIntensity(room, intensity));
                }
            }
        }

        normalizeIntensity();

        dungeon.FindBoss().SetIntensity(1.0);
        MZRoom goalRoom = dungeon.FindGoal();
        if (goalRoom != null)
            goalRoom.SetIntensity(0.0);
    }

    /**
     * Checks with the
     * {@link net.bytten.metazelda.constraints.IMZDungeonConstraints} that the
     * dungeon is OK to use.
     *
     * @throws RetryException if the IMZDungeonConstraints decided generation must
     *                        be re-attempted
     * @see net.bytten.metazelda.constraints.IMZDungeonConstraints
     */
    protected void checkAcceptable() throws RetryException {
        if (!constraints.isAcceptable(dungeon))
            throw new RetryException();
    }

    @Override
    public void Generate() {
        int attempt = 0;

        while (true) {
            try {
                KeyLevelRoomMapping levels;
                int roomsPerLock;
                if (constraints.GetMaxKeys() > 0) {
                    roomsPerLock = constraints.GetMaxRooms() /
                        constraints.GetMaxKeys();
                } else {
                    roomsPerLock = constraints.GetMaxRooms();
                }
                while (true) {
                    dungeon = new MZDungeon();

                    // Maps keyLevel -> MZRooms that were created when lockCount had that
                    // value
                    levels = new KeyLevelRoomMapping();

                    // Create the entrance to the dungeon:
                    initEntranceRoom(levels);

                    try {
                        // Fill the dungeon with rooms:
                        placeRooms(levels, roomsPerLock);
                        break;
                    } catch (OutOfRoomsException e) {
                        // We can run out of rooms where certain links have
                        // predetermined locks. Example: if a river bisects the
                        // map, the keyLevel for rooms in the river > 0 because
                        // crossing water requires a key. If there are not
                        // enough rooms before the river to build up to the
                        // key for the river, we've run out of rooms.
                        log("Ran out of rooms. roomsPerLock was "+roomsPerLock);
                        roomsPerLock = roomsPerLock * constraints.GetMaxKeys() /
                                (constraints.GetMaxKeys() + 1);
                        log("roomsPerLock is now "+roomsPerLock);

                        if (roomsPerLock == 0) {
                            throw new MZGenerationFailureException(
                                    "Failed to place rooms. Have you forgotten to disable boss-locking?");
                            // If the boss room is locked, the final key is used
                            // only for the boss room. So if the final key is
                            // also used to cross the river, rooms cannot be
                            // placed.
                        }
                    }
                }

                // Place the boss and goal rooms:
                placeBossGoalRooms(levels);

                // Place switches and the locks that require it:
                placeSwitches();

                computeIntensity(levels);

                // Place the keys within the dungeon:
                placeKeys(levels);

                if (levels.keyCount()-1 != constraints.GetMaxKeys())
                    throw new RetryException();

                // Make the dungeon less tree-like:
                graphify();

                checkAcceptable();

                return;

            } catch (RetryException e) {
                if (++ attempt > maxRetries) {
                    throw new MZGenerationFailureException("MZDungeon generator failed", e);
                }
                log("Retrying dungeon generation...");
            }
        }

    }

    @Override
    public IMZDungeon GetMZDungeon() {
        return dungeon;
    }

    public bool IsBossRoomLocked() {
        return bossRoomLocked;
    }

    public void setBossRoomLocked(bool bossRoomLocked) {
        this.bossRoomLocked = bossRoomLocked;
    }

    public bool isGenerateGoal() {
        return GenerateGoal;
    }

    public void setGenerateGoal(bool GenerateGoal) {
        this.GenerateGoal = GenerateGoal;
    }

}
